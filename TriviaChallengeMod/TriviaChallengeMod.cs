using System;
using System.IO;
using System.Linq;
using System.Text;
using System.Collections.Generic;
using System.Threading;
using Newtonsoft.Json;
using Eleon.Modding; // API2 types (MessageData, IModApi)
using Eleon;          // API1 types (CmdId, ChatInfo, PString, MsgChannel)

// Hybrid mod: API2 for messaging; API1 for events and console command execution.
public class TriviaChallengeMod : IMod, ModInterface
{
    // === API bridges ===
    private IModApi api2;     // API2 object (SendChatMessage)
    private ModGameAPI api1;  // API1 object (events, console command)

    // === Paths / IO ===
    private string modDir;
    private string cfgPath;
    private string bankPath;
    private string statePath;
    private string logPath;

    // === Runtime ===
    private TriviaConfig cfg;
    private QuestionBank bank;
    private Timer loopTimer;
    private readonly object stateLock = new object();

    // Round state
    private TriviaPhase phase = TriviaPhase.Idle;
    private DateTime phaseUntilUtc = DateTime.MinValue;
    private DateTime nextTickUtc = DateTime.MinValue;
    private HashSet<int> joined = new HashSet<int>();
    private Dictionary<int, int> score = new Dictionary<int, int>();
    private int currentQIndex = -1; // 0..4
    private RoundQuestion[] roundQs; // length cfg.questionCount
    private HashSet<int> answeredThisQ = new HashSet<int>();
    private Dictionary<int, DateTime> answerRateLimit = new Dictionary<int, DateTime>();
    private LruCache<string> recentQIds;

    // Claim persistence
    private PersistedRound persisted = new PersistedRound();

    // Dry-run state
    private bool dryRun = false;
    private int dryRunStarterPid = -1;

    // Constants
    private const string RoundIdFormat = "yyyy-MM-ddTHH:mm:ssZ";
    private const int MinPlayers = 3; // per spec

    // ================= API2 =================
    public void Init(IModApi api)
    {
        api2 = api;
        modDir = Path.GetDirectoryName(System.Reflection.Assembly.GetExecutingAssembly().Location);
        cfgPath = Path.Combine(modDir, "trivia_config.json");
        bankPath = Path.Combine(modDir, "questions.json");
        statePath = Path.Combine(modDir, "trivia_state.json");
        logPath = Path.Combine(modDir, "trivia.log");

        SafeLog("[Init] TriviaChallengeMod starting (API2).");
        LoadConfig();
        LoadBank();
        LoadState();

        recentQIds = new LruCache<string>(Math.Max(cfg.noRepeatWindow, cfg.questionCount * 2));

        // 1s loop tick
        loopTimer = new Timer(Loop, null, TimeSpan.FromSeconds(1), TimeSpan.FromSeconds(1));
        SafeLog("[Init] Loop timer armed.");

        // Start idle; scheduler will flip to Joining at the hour boundary
        phase = TriviaPhase.Idle;
        SafeLog("[Init] Ready (Idle).");
    }

    public void Shutdown()
    {
        loopTimer?.Dispose();
        SafeLog("[Shutdown] TriviaChallengeMod stopped.");
    }

    // ================= API1 =================
    public void Game_Start(ModGameAPI dediAPI)
    {
        api1 = dediAPI;
        SafeLog("[Game_Start] API1 attached (events enabled).");
    }

    public void Game_Exit() { SafeLog("[Game_Exit] API1 shutdown."); }
    public void Game_Update() { }

    public void Game_Event(CmdId eventId, ushort seqNr, object data)
    {
        try
        {
            if (eventId == CmdId.Event_ChatMessage && data is ChatInfo chat)
            {
                var raw = chat.msg?.Trim() ?? string.Empty;
                if (raw.Length == 0) return;

                int pid = chat.playerId;
                var msg = raw;

                // Player commands
                if (msg.StartsWith("/trivia join", StringComparison.OrdinalIgnoreCase))
                {
                    HandleJoin(pid);
                }
                else if (msg.StartsWith("/a ", StringComparison.OrdinalIgnoreCase) || msg.Equals("/a", StringComparison.OrdinalIgnoreCase))
                {
                    var parts = msg.Split(new[] { ' ' }, StringSplitOptions.RemoveEmptyEntries);
                    string letter = (parts.Length >= 2 ? parts[1] : "").ToUpperInvariant();
                    HandleAnswer(pid, letter);
                }
                else if (msg.Equals("/claim", StringComparison.OrdinalIgnoreCase))
                {
                    HandleClaim(pid);
                }
                // Admin controls (no special admin gating per your note)
                else if (msg.Equals("/trivia start", StringComparison.OrdinalIgnoreCase))
                {
                    ForceStart();
                }
                else if (msg.Equals("/trivia dryrun", StringComparison.OrdinalIgnoreCase))
                {
                    ForceDryRun(pid);
                }
                else if (msg.Equals("/trivia stop", StringComparison.OrdinalIgnoreCase))
                {
                    ForceStop();
                }
                else if (msg.Equals("/trivia status", StringComparison.OrdinalIgnoreCase))
                {
                    SendPrivate(pid, GetStatusLine());
                }
                else if (msg.Equals("/trivia reload", StringComparison.OrdinalIgnoreCase))
                {
                    LoadConfig();
                    LoadBank();
                    SendPrivate(pid, "[TRIVIA] Config & questions reloaded.");
                }
                else if (msg.StartsWith("/trivia reward ", StringComparison.OrdinalIgnoreCase))
                {
                    var sp = msg.Split(new[]{' '}, StringSplitOptions.RemoveEmptyEntries);
                    if (sp.Length == 3 && int.TryParse(sp[2], out var cr) && cr >= 0)
                    {
                        cfg.reward.credits = cr;
                        SaveConfig();
                        SendPrivate(pid, $"[TRIVIA] Reward set to {cr} cr.");
                    }
                }
            }
        }
        catch (Exception ex)
        {
            SafeLog($"[Game_Event] {ex}");
        }
    }

    // ================= Loop / Scheduler =================
    private void Loop(object _)
    {
        try
        {
            lock (stateLock)
            {
                var now = DateTime.UtcNow;

                // Hourly scheduler (top of the hour by default)
                if (phase == TriviaPhase.Idle)
                {
                    if (ShouldKickHourly(now))
                    {
                        BeginJoining(now, false, -1);
                        return;
                    }
                    return;
                }

                // Handle periodic ticks (every cfg.tickEverySeconds)
                if (cfg.tickEverySeconds > 0 && now >= nextTickUtc)
                {
                    OnTick(now);
                    nextTickUtc = now.AddSeconds(cfg.tickEverySeconds);
                }

                // Phase transitions on deadline
                if (now >= phaseUntilUtc)
                {
                    switch (phase)
                    {
                        case TriviaPhase.Joining:
                            StartRoundOrAbort(now);
                            break;
                        case TriviaPhase.Asking:
                            // end question time -> reveal
                            BeginReveal(now);
                            break;
                        case TriviaPhase.Reveal:
                            NextOrFinish(now);
                            break;
                        case TriviaPhase.Finished:
                            // immediately publish winners -> move to Claimable
                            AnnounceWinners(now);
                            BeginClaimable(now);
                            break;
                        case TriviaPhase.Claimable:
                            // end of claim window -> idle
                            phase = TriviaPhase.Idle;
                            SafeLog("[Loop] Claim window closed -> Idle.");
                            break;
                    }
                }
            }
        }
        catch (Exception ex)
        {
            SafeLog($"[Loop] {ex}");
        }
    }

    private bool ShouldKickHourly(DateTime nowUtc)
    {
        if (cfg.schedule?.mode == "hourly")
        {
            // Start when minute==cfg.schedule.minute and second==0
            return nowUtc.Minute == cfg.schedule.minute && nowUtc.Second == 0;
        }
        return false;
    }

    private void BeginJoining(DateTime now, bool isDryRun, int starterPid)
    {
        phase = TriviaPhase.Joining;
        dryRun = isDryRun;
        dryRunStarterPid = starterPid;

        joined.Clear();
        score.Clear();
        currentQIndex = -1;
        roundQs = null;
        answeredThisQ.Clear();

        // Auto-join the starter in dry-run for convenience
        if (dryRun && starterPid > 0)
        {
            joined.Add(starterPid);
            score[starterPid] = 0;
            SendPrivate(starterPid, "[TRIVIA] Dry-run: you are auto-joined.");
        }

        var joinSeconds = Math.Max(5, cfg.joinWindowSeconds);
        phaseUntilUtc = now.AddSeconds(joinSeconds);
        nextTickUtc = now; // fire tick immediately

        var ann = cfg.messages.announce.Replace("{join}", joinSeconds.ToString());
        if (dryRun) ann += cfg.messages.dryRunTag;
        Broadcast(ann);
        SafeLog($"[BeginJoining] Join window open {joinSeconds}s (dryRun={dryRun}).");
    }

    private void BeginJoining(DateTime now)
    => BeginJoining(now, false, -1);

    private void StartRoundOrAbort(DateTime now)
    {
        if (!dryRun && joined.Count < MinPlayers)
        {
            Broadcast($"[TRIVIA] Not enough players (need {MinPlayers}, got {joined.Count}). Round canceled.");
            phase = TriviaPhase.Idle;
            SafeLog($"[StartRoundOrAbort] Canceled: {joined.Count} < {MinPlayers}.");
            return;
        }

        // Select questions honoring noRepeatWindow
        var pick = bank.Pick(cfg.questionCount, recentQIds);
        if (pick == null || pick.Length == 0)
        {
            Broadcast("[TRIVIA] No questions available - contact admin.");
            phase = TriviaPhase.Idle;
            return;
        }
        roundQs = pick;
        foreach (var rq in roundQs) recentQIds.Push(rq.q.id);

        currentQIndex = 0;
        BeginQuestion(now, currentQIndex);
    }

    private void BeginQuestion(DateTime now, int qi)
    {
        answeredThisQ.Clear();
        var rq = roundQs[qi];

        Broadcast(cfg.messages.q
        .Replace("{n}", (qi + 1).ToString())
        .Replace("{question}", rq.q.q));

        Broadcast(cfg.messages.opts
        .Replace("{A}", rq.choices[0])
        .Replace("{B}", rq.choices[1])
        .Replace("{C}", rq.choices[2])
        .Replace("{D}", rq.choices[3]));

        phase = TriviaPhase.Asking;
        phaseUntilUtc = now.AddSeconds(cfg.questionTimeSeconds);
        nextTickUtc = now.AddSeconds(cfg.tickEverySeconds);
        SafeLog($"[BeginQuestion] Q{qi+1}/{cfg.questionCount} ends at {phaseUntilUtc:o}.");
    }

    private void BeginReveal(DateTime now)
    {
        var rq = roundQs[currentQIndex];
        Broadcast(cfg.messages.reveal
        .Replace("{letter}", rq.correctLetter)
        .Replace("{text}", rq.q.choices[rq.q.answer]));
        phase = TriviaPhase.Reveal;
        phaseUntilUtc = now.AddSeconds(2); // short pause
    }

    private void NextOrFinish(DateTime now)
    {
        if (currentQIndex + 1 < cfg.questionCount)
        {
            currentQIndex++;
            BeginQuestion(now, currentQIndex);
        }
        else
        {
            phase = TriviaPhase.Finished;
            phaseUntilUtc = now.AddSeconds(1);
        }
    }

    private void AnnounceWinners(DateTime now)
    {
        int maxScore = score.Count == 0 ? 0 : score.Values.DefaultIfEmpty(0).Max();
        var winners = score.Where(kv => kv.Value == maxScore && maxScore > 0)
        .Select(kv => kv.Key)
        .ToList();

        if (winners.Count == 0 && !cfg.allowZeroScoreWinners)
        {
            var msg = cfg.messages.roundNoWin;
            if (dryRun) msg += cfg.messages.dryRunTag;
            Broadcast(msg);
            persisted = new PersistedRound { roundId = NowRoundId(now), winners = new List<int>(), claimed = new List<int>() };
            SaveState();
            return;
        }

        string winnersList = winners.Count == 0 ? "(no correct answers)"
        : string.Join(", ", winners.Select(ResolveDisplayName));
        var winMsg = cfg.messages.roundWin
        .Replace("{score}", maxScore.ToString())
        .Replace("{winners}", winnersList);
        if (dryRun) winMsg += cfg.messages.dryRunTag;
        Broadcast(winMsg);

        // Persist claimable winners even in dry-run (claims will be denied)
        persisted = new PersistedRound
        {
            roundId = NowRoundId(now),
            winners = winners.ToList(),
            claimed = new List<int>()
        };
        SaveState();

        if (dryRun)
        {
            Broadcast(cfg.messages.dryRunClaimNotice);
        }
        else
        {
            Broadcast(cfg.messages.claimable
            .Replace("{credits}", cfg.reward.credits.ToString()));
        }
    }

    private void BeginClaimable(DateTime now)
    {
        phase = TriviaPhase.Claimable;
        var minutes = Math.Max(5, 55);
        phaseUntilUtc = now.AddMinutes(minutes);
        SafeLog($"[BeginClaimable] Claims open for ~{minutes} minutes (dryRun={dryRun}).");
    }

    private void OnTick(DateTime now)
    {
        int secsLeft = Math.Max(0, (int)Math.Ceiling((phaseUntilUtc - now).TotalSeconds));
        if (secsLeft == 0) return;

        if (phase == TriviaPhase.Joining)
        {
            Broadcast(cfg.messages.joinTick
            .Replace("{secs}", secsLeft.ToString())
            .Replace("{count}", joined.Count.ToString()));
        }
        else if (phase == TriviaPhase.Asking)
        {
            Broadcast(cfg.messages.tick.Replace("{secs}", secsLeft.ToString()));
        }
    }

    // ================= Commands =================
    private void HandleJoin(int playerId)
    {
        lock (stateLock)
        {
            if (phase != TriviaPhase.Joining)
            {
                SendPrivate(playerId, "[TRIVIA] No active join window.");
                return;
            }
            if (!joined.Contains(playerId))
            {
                joined.Add(playerId);
                score[playerId] = 0;
                SendPrivate(playerId, cfg.messages.joinConfirm + (dryRun ? cfg.messages.dryRunTag : string.Empty));
            }
        }
    }

    private void HandleAnswer(int playerId, string letter)
    {
        lock (stateLock)
        {
            if (phase != TriviaPhase.Asking)
            {
                SendPrivate(playerId, "[TRIVIA] No active question.");
                return;
            }
            if (!joined.Contains(playerId))
            {
                SendPrivate(playerId, "[TRIVIA] You are not joined. Use /trivia join during the window.");
                return;
            }
            // Rate limit (2s)
            if (answerRateLimit.TryGetValue(playerId, out var until) && DateTime.UtcNow < until)
            {
                return; // ignore spam
            }
            answerRateLimit[playerId] = DateTime.UtcNow.AddSeconds(2);

            if (answeredThisQ.Contains(playerId))
            {
                SendPrivate(playerId, "[TRIVIA] You already answered this question.");
                return;
            }

            var rq = roundQs[currentQIndex];
            var upper = (letter ?? "").Trim().ToUpperInvariant();
            if (upper != "A" && upper != "B" && upper != "C" && upper != "D")
            {
                SendPrivate(playerId, "[TRIVIA] Use /a <A|B|C|D>.");
                return;
            }

            answeredThisQ.Add(playerId);

            if (upper == rq.correctLetter)
            {
                score[playerId] = score.TryGetValue(playerId, out var s) ? s + 1 : 1;
            }

            // Fast-forward if everyone answered
            if (answeredThisQ.Count >= joined.Count)
            {
                phaseUntilUtc = DateTime.UtcNow;
            }
        }
    }

    private void HandleClaim(int playerId)
    {
        lock (stateLock)
        {
            if (persisted?.roundId == null)
            {
                SendPrivate(playerId, "[TRIVIA] Nothing to claim right now.");
                return;
            }
            if (dryRun)
            {
                SendPrivate(playerId, cfg.messages.dryRunClaimDenied);
                return;
            }
            if (!persisted.winners.Contains(playerId))
            {
                SendPrivate(playerId, cfg.messages.notWinner);
                return;
            }
            if (persisted.claimed.Contains(playerId))
            {
                SendPrivate(playerId, cfg.messages.alreadyClaimed);
                return;
            }

            bool ok = AwardCredits(playerId, cfg.reward.credits);
            if (ok)
            {
                persisted.claimed.Add(playerId);
                SaveState();
                Broadcast(cfg.messages.claimed
                .Replace("{player}", ResolveDisplayName(playerId))
                .Replace("{credits}", cfg.reward.credits.ToString()));
            }
            else
            {
                SendPrivate(playerId, "[TRIVIA] Failed to grant credits. Contact admin.");
            }
        }
    }

    private void ForceStart()
    {
        lock (stateLock)
        {
            if (phase != TriviaPhase.Idle) return;
            BeginJoining(DateTime.UtcNow, false, -1);
        }
    }

    private void ForceDryRun(int starterPid)
    {
        lock (stateLock)
        {
            if (phase != TriviaPhase.Idle) return;
            BeginJoining(DateTime.UtcNow, true, starterPid);
        }
    }

    private void ForceStop()
    {
        lock (stateLock)
        {
            phase = TriviaPhase.Idle;
            dryRun = false;
            dryRunStarterPid = -1;
            Broadcast("[TRIVIA] Round aborted by admin.");
        }
    }

    private string GetStatusLine()
    {
        var tag = dryRun ? " (dry-run)" : string.Empty;
        if (phase == TriviaPhase.Idle)
            return "[TRIVIA] Idle. Next start: top of the hour.";
        if (phase == TriviaPhase.Joining)
            return $"[TRIVIA] Joining{tag}: {joined.Count} joined.";
        if (phase == TriviaPhase.Asking)
            return $"[TRIVIA] Question {currentQIndex + 1}/{cfg.questionCount}{tag}. Players: {joined.Count}.";
        if (phase == TriviaPhase.Reveal)
            return $"[TRIVIA] Revealing Q{currentQIndex + 1}{tag}.";
        if (phase == TriviaPhase.Finished)
            return $"[TRIVIA] Computing winners{tag}...";
        if (phase == TriviaPhase.Claimable)
            return $"[TRIVIA] Winners may /claim{tag}.";
        return "[TRIVIA] Unknown state.";
    }

    // ================= Rewards =================
    // Execute console command via API1 (supported across builds).
    // Example template: "credits add {playerId} {amount}"
    private bool AwardCredits(int playerId, int amount)
    {
        try
        {
            if (amount <= 0) return true; // allow zero-credit test runs
            if (string.IsNullOrWhiteSpace(cfg.reward.consoleCommandTemplate))
            {
                SafeLog("[AwardCredits] No consoleCommandTemplate set.");
                return false;
            }

            string cmd = cfg.reward.consoleCommandTemplate
            .Replace("{playerId}", playerId.ToString())
            .Replace("{amount}", amount.ToString());

            // API1 console command execution
            api1.Game_Request(CmdId.Request_ConsoleCommand, (ushort)DateTime.UtcNow.Millisecond, new PString(cmd));
            SafeLog($"[AwardCredits] Executed console: {cmd}");
            return true;
        }
        catch (Exception ex)
        {
            SafeLog($"[AwardCredits] {ex}");
            return false;
        }
    }

    // ================= IO / Helpers =================
    private void LoadConfig()
    {
        try
        {
            if (!File.Exists(cfgPath))
            {
                cfg = TriviaConfig.Default();
                File.WriteAllText(cfgPath, JsonConvert.SerializeObject(cfg, Formatting.Indented));
                SafeLog($"[LoadConfig] Created default {cfgPath}");
            }
            else
            {
                cfg = JsonConvert.DeserializeObject<TriviaConfig>(File.ReadAllText(cfgPath)) ?? TriviaConfig.Default();
                SafeLog($"[LoadConfig] Loaded {cfgPath}");
            }
        }
        catch (Exception ex)
        {
            SafeLog($"[LoadConfig] {ex} - using defaults.");
            cfg = TriviaConfig.Default();
        }
    }

    private void SaveConfig()
    {
        try { File.WriteAllText(cfgPath, JsonConvert.SerializeObject(cfg, Formatting.Indented)); }
        catch (Exception ex) { SafeLog($"[SaveConfig] {ex}"); }
    }

    private void LoadBank()
    {
        try
        {
            if (!File.Exists(bankPath))
            {
                var seed = QuestionBank.SeedGaming();
                File.WriteAllText(bankPath, JsonConvert.SerializeObject(seed, Formatting.Indented));
                bank = new QuestionBank(seed);
                SafeLog($"[LoadBank] Seeded questions.json with {seed.questions.Count} Qs.");
            }
            else
            {
                var raw = JsonConvert.DeserializeObject<QuestionFile>(File.ReadAllText(bankPath));
                bank = new QuestionBank(raw);
                SafeLog($"[LoadBank] Loaded {bank.Count} questions.");
            }
        }
        catch (Exception ex)
        {
            SafeLog($"[LoadBank] {ex} - creating empty bank.");
            bank = new QuestionBank(new QuestionFile { version = 1, questions = new List<Question>() });
        }
    }

    private void LoadState()
    {
        try
        {
            if (File.Exists(statePath))
            {
                persisted = JsonConvert.DeserializeObject<PersistedRound>(File.ReadAllText(statePath)) ?? new PersistedRound();
                SafeLog($"[LoadState] Last round id: {persisted.roundId}, winners: {persisted.winners?.Count ?? 0}, claimed: {persisted.claimed?.Count ?? 0}");
            }
            else
            {
                persisted = new PersistedRound();
            }
        }
        catch (Exception ex)
        {
            SafeLog($"[LoadState] {ex}");
            persisted = new PersistedRound();
        }
    }

    private void SaveState()
    {
        try { File.WriteAllText(statePath, JsonConvert.SerializeObject(persisted, Formatting.Indented)); }
        catch (Exception ex) { SafeLog($"[SaveState] {ex}"); }
    }

    private void Broadcast(string text)
    {
        try
        {
            var msg = new MessageData
            {
                Channel = Eleon.MsgChannel.Global, // disambiguate namespace
                Text = text,
                SenderNameOverride = "Trivia"
            };
            api2.Application.SendChatMessage(msg);
        }
        catch (Exception ex)
        {
            SafeLog($"[Broadcast] {ex}");
        }
    }

    private void SendPrivate(int playerId, string text)
    {
        try
        {
            var msg = new MessageData
            {
                Channel = Eleon.MsgChannel.SinglePlayer, // disambiguate namespace
                RecipientEntityId = playerId,
                Text = text,
                SenderNameOverride = "Trivia"
            };
            api2.Application.SendChatMessage(msg);
        }
        catch (Exception ex)
        {
            SafeLog($"[SendPrivate] {ex}");
        }
    }

    private string ResolveDisplayName(int playerId)
    {
        // Minimal: if you want actual names, wire up a player registry lookup.
        return $"Player_{playerId}";
    }

    private static string NowRoundId(DateTime nowUtc) => nowUtc.ToString(RoundIdFormat);

    private void SafeLog(string line)
    {
        try
        {
            File.AppendAllText(logPath, $"{DateTime.UtcNow:yyyy-MM-dd HH:mm:ss} {line}\\n");
        }
        catch { }
    }

    // ================= Models =================
    private enum TriviaPhase { Idle, Joining, Asking, Reveal, Finished, Claimable }

    private class TriviaConfig
    {
        public Schedule schedule { get; set; } = new Schedule { mode = "hourly", minute = 0 };
        public int joinWindowSeconds { get; set; } = 60;
        public int questionCount { get; set; } = 5;
        public int questionTimeSeconds { get; set; } = 30;
        public int tickEverySeconds { get; set; } = 5;
        public int noRepeatWindow { get; set; } = 40;
        public bool allowZeroScoreWinners { get; set; } = false;
        public Reward reward { get; set; } = new Reward { credits = 50000, consoleCommandTemplate = "credits add {playerId} {amount}" };
        public Messages messages { get; set; } = Messages.Default();

        public static TriviaConfig Default() => new TriviaConfig();
    }

    private class Schedule { public string mode { get; set; } = "hourly"; public int minute { get; set; } = 0; }

    private class Reward { public int credits { get; set; } = 50000; public string consoleCommandTemplate { get; set; } = "credits add {playerId} {amount}"; }

    private class Messages
    {
        public string announce { get; set; }
        public string joinConfirm { get; set; }
        public string joinTick { get; set; }
        public string q { get; set; }
        public string opts { get; set; }
        public string tick { get; set; }
        public string reveal { get; set; }
        public string roundWin { get; set; }
        public string roundNoWin { get; set; }
        public string claimable { get; set; }
        public string claimed { get; set; }
        public string alreadyClaimed { get; set; }
        public string notWinner { get; set; }
        public string dryRunTag { get; set; }
        public string dryRunClaimNotice { get; set; }
        public string dryRunClaimDenied { get; set; }

        public static Messages Default() => new Messages
        {
            announce = "[TRIVIA] New challenge in {join}s! Type /trivia join to participate.",
            joinConfirm = "[TRIVIA] You're in. Get ready.",
            joinTick = "[TRIVIA] Starting in {secs}s... ({count} joined)",
            q = "[TRIVIA Q{n}] {question}",
            opts = "A) {A}  B) {B}  C) {C}  D) {D} -- answer with /a <letter>",
            tick = "[TRIVIA] {secs}s left...",
            reveal = "[TRIVIA] Time! Correct: {letter}) {text}",
            roundWin = "[TRIVIA] Winners with {score}/5: {winners}",
            roundNoWin = "[TRIVIA] No winners this time.",
            claimable = "[TRIVIA] Winners: use /claim to receive {credits} credits.",
            claimed = "[TRIVIA] {player} claimed {credits} cr.",
            alreadyClaimed = "[TRIVIA] You already claimed this round.",
            notWinner = "[TRIVIA] You're not on the winners list this round.",
            dryRunTag = " [DRY-RUN: no payouts]",
            dryRunClaimNotice = "[TRIVIA] Dry-run complete. No payouts in test mode.",
            dryRunClaimDenied = "[TRIVIA] Dry-run: payouts are disabled."
        };
    }

    private class PersistedRound
    {
        public string roundId { get; set; }
        public List<int> winners { get; set; } = new List<int>();
        public List<int> claimed { get; set; } = new List<int>();
    }

    // ================= Questions =================
    private class QuestionBank
    {
        private readonly List<Question> qs;
        private readonly Random rng = new Random();

        public int Count => qs?.Count ?? 0;

        public QuestionBank(QuestionFile f)
        {
            qs = f?.questions ?? new List<Question>();
        }

        public RoundQuestion[] Pick(int count, LruCache<string> lru)
        {
            if (qs.Count == 0) return Array.Empty<RoundQuestion>();

            // Filter out recent IDs (noRepeatWindow)
            var candidates = qs.Where(q => !lru.Contains(q.id)).ToList();
            if (candidates.Count < count) candidates = qs.ToList(); // fallback

            // Shuffle and take
            Shuffle(candidates);
            var take = candidates.Take(count).ToList();

            var list = new List<RoundQuestion>(take.Count);
            foreach (var q in take)
            {
                int idx = q.answer;
                var choices = q.choices.ToArray();
                var letters = new[] { "A", "B", "C", "D" };

                var order = new List<int> { 0, 1, 2, 3 };
                Shuffle(order);
                string correctLetter = null;
                var shuffled = new string[4];
                for (int i = 0; i < 4; i++)
                {
                    int src = order[i];
                    shuffled[i] = choices[src];
                    if (src == idx) correctLetter = letters[i];
                }
                list.Add(new RoundQuestion { q = q, choices = shuffled, correctLetter = correctLetter });
            }
            return list.ToArray();
        }

        private void Shuffle<T>(IList<T> list)
        {
            for (int i = list.Count - 1; i > 0; i--)
            {
                int j = rng.Next(i + 1);
                (list[i], list[j]) = (list[j], list[i]);
            }
        }

        public static QuestionFile SeedGaming()
        {
            return new QuestionFile
            {
                version = 1,
                questions = new List<Question>
                {
                    new Question{ id="g-001", cat="gaming", q="Which company developed the original Half-Life (1998)?", choices=new List<string>{"Valve","id Software","Epic Games","3D Realms"}, answer=0, difficulty=1 },
                    new Question{ id="g-002", cat="gaming", q="In Minecraft, which ore requires an iron pickaxe or better to mine?", choices=new List<string>{"Diamond Ore","Coal Ore","Copper Ore","Redstone Ore"}, answer=0, difficulty=1 },
                    new Question{ id="g-003", cat="gaming", q="The Konami Code starts with:", choices=new List<string>{"Up, Up, Down, Down","Left, Left, Right, Right","A, B, A, B","Down, Down, Up, Up"}, answer=0, difficulty=1 },
                    new Question{ id="g-004", cat="gaming", q="Which game popularized the phrase 'The Cake is a Lie'?", choices=new List<string>{"Portal","BioShock","Mass Effect","GLaDOS Quest"}, answer=0, difficulty=1 },
                    new Question{ id="g-005", cat="gaming", q="In The Witcher 3, what is Geralt's last name?", choices=new List<string>{"of Rivia","of Kaer Morhen","the White","Wolf"}, answer=0, difficulty=1 }
                }
            };
        }
    }

    private class QuestionFile { public int version { get; set; } = 1; public List<Question> questions { get; set; } = new List<Question>(); }
    private class Question
    {
        public string id { get; set; }
        public string cat { get; set; } = "gaming";
        public string q { get; set; }
        public List<string> choices { get; set; } // 4 items
        public int answer { get; set; } // index 0..3 in original choices
        public int difficulty { get; set; } = 1;
    }

    private class RoundQuestion
    {
        public Question q;
        public string[] choices; // shuffled 4
        public string correctLetter; // "A"/"B"/"C"/"D"
    }

    // Small LRU for recent question IDs
    private class LruCache<T>
    {
        private readonly int cap;
        private readonly LinkedList<T> list = new LinkedList<T>();
        private readonly HashSet<T> set = new HashSet<T>();
        public LruCache(int capacity) { cap = Math.Max(1, capacity); }
        public bool Contains(T t) => set.Contains(t);
        public void Push(T t)
        {
            if (set.Contains(t)) return;
            list.AddFirst(t);
            set.Add(t);
            if (list.Count > cap)
            {
                var last = list.Last.Value;
                list.RemoveLast();
                set.Remove(last);
            }
        }
    }
}
