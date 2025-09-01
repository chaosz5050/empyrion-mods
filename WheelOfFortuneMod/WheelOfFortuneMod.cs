using System;
using System.IO;
using System.Linq;
using System.Collections.Generic;
using System.Collections.Concurrent;
using System.Threading.Tasks;
using Newtonsoft.Json;
using Eleon.Modding; // API2: messaging
using Eleon;          // API1: events & console (CmdId, ChatInfo, PString, etc.)

public class WheelOfFortuneMod : IMod, ModInterface
{
    // === API bridges ===
    private IModApi api2;     // API2 object (SendChatMessage)
    private ModGameAPI api1;  // API1 object (events, console command)

    // === Paths / IO ===
    private string modDir;
    private string cfgPath;
    private string stateDir;
    private string logPath;

    // === Runtime ===
    private WheelConfig cfg;
    private readonly object ioLock = new object();
    private static readonly Random rng = new Random();
    
    // === Player Name Resolution ===
    private ConcurrentDictionary<int, string> playerNameCache = new ConcurrentDictionary<int, string>();
    private ConcurrentDictionary<int, DateTime> pendingPlayerLookups = new ConcurrentDictionary<int, DateTime>();

    // ================= IMod (API2) =================
    public void Init(IModApi api)
    {
        api2 = api;

        modDir   = Path.GetDirectoryName(System.Reflection.Assembly.GetExecutingAssembly().Location);
        cfgPath  = Path.Combine(modDir, "wheel_config.json");
        stateDir = Path.Combine(modDir, "playerdata");
        logPath  = Path.Combine(modDir, "wheel.log");

        Directory.CreateDirectory(stateDir);

        SafeLog("[Init] WheelOfFortuneMod starting (API2).");
        LoadConfig();
        SafeLog("[Init] Ready.");
    }

    public void Shutdown()
    {
        SafeLog("[Shutdown] WheelOfFortuneMod stopped.");
    }

    // ================= ModInterface (API1) =================
    public void Game_Start(ModGameAPI dediAPI)
    {
        api1 = dediAPI;
        SafeLog("[Game_Start] API1 attached.");
    }

    public void Game_Exit()   { SafeLog("[Game_Exit] API1 shutdown."); }
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

                if (msg.Equals("/wheel", StringComparison.OrdinalIgnoreCase))
                {
                    HandleSpinCommand(pid);
                }
                else if (msg.Equals("/wheel status", StringComparison.OrdinalIgnoreCase))
                {
                    HandleStatus(pid);
                }
            }
            else if (eventId == CmdId.Event_Player_Info && data is PlayerInfo playerInfo)
            {
                // Cache player name when we receive player info responses
                if (playerInfo != null && playerInfo.entityId > 0 && !string.IsNullOrEmpty(playerInfo.playerName))
                {
                    CachePlayerName(playerInfo.entityId, playerInfo.playerName);
                    SafeLog($"[Game_Event] Cached player info: {playerInfo.entityId} -> {playerInfo.playerName}");
                }
            }
        }
        catch (Exception ex)
        {
            SafeLog($"[Game_Event] {ex}");
        }
    }

    // ================= Core Logic =================
    private void HandleSpinCommand(int playerId)
    {
        try
        {
            var prof = LoadOrCreateProfile(playerId);
            ResetIfNewDay(prof);

            // cooldown check
            var now = DateTime.UtcNow;
            if (prof.lastSpinUtc.HasValue)
            {
                var nextAllowed = prof.lastSpinUtc.Value.AddSeconds(cfg.perPlayer.cooldownSeconds);
                if (now < nextAllowed)
                {
                    int secs = (int)Math.Ceiling((nextAllowed - now).TotalSeconds);
                    SendPrivate(playerId, (cfg.messages.cooldown ?? "[WHEEL] Cooldown active: {secs}s.")
                    .Replace("{secs}", secs.ToString()));
                    return;
                }
            }

            // daily limit check
            if (prof.spinsToday >= cfg.perPlayer.maxSpinsPerDay)
            {
                SendPrivate(playerId, (cfg.messages.limit ?? "[WHEEL] Limit reached: {count}")
                .Replace("{count}", cfg.perPlayer.maxSpinsPerDay.ToString()));
                return;
            }

            // prize pool validation
            if (!ValidatePrizePool())
            {
                SendPrivate(playerId, cfg.messages.adminError ?? "[WHEEL] Hey admin, your wheel is broken! Need at least 3 prizes. What kind of wheel has 0-2 options? That's not a wheel, that's a coin flip!");
                return;
            }

            var playerName = ResolveDisplayName(playerId);
            Broadcast((cfg.messages.spinStart ?? "[WHEEL] Spinning for {player}…").Replace("{player}", playerName));

            // pick a prize
            var prize = PickRandomPrize();

            // announce
            Broadcast((cfg.messages.won ?? "[WHEEL] {player} won {reward}!")
            .Replace("{player}", playerName)
            .Replace("{reward}", prize.label ?? PrizeToText(prize)));

            // award
            bool awarded = AwardPrize(playerId, prize);
            if (!awarded)
            {
                SendPrivate(playerId, cfg.messages.error ?? "[WHEEL] Error.");
                return;
            }

            // persist usage
            prof.spinsToday += 1;
            prof.lastSpinUtc = now;
            SaveProfile(playerId, prof);
        }
        catch (Exception ex)
        {
            SafeLog($"[HandleSpinCommand] {ex}");
            SendPrivate(playerId, cfg.messages.error ?? "[WHEEL] Error.");
        }
    }

    private void HandleStatus(int playerId)
    {
        try
        {
            var prof = LoadOrCreateProfile(playerId);
            ResetIfNewDay(prof);

            int remaining = Math.Max(0, cfg.perPlayer.maxSpinsPerDay - prof.spinsToday);
            int cooldown = 0;
            if (prof.lastSpinUtc.HasValue)
            {
                var now = DateTime.UtcNow;
                var nextAllowed = prof.lastSpinUtc.Value.AddSeconds(cfg.perPlayer.cooldownSeconds);
                cooldown = Math.Max(0, (int)Math.Ceiling((nextAllowed - now).TotalSeconds));
            }

            SendPrivate(playerId, $"[WHEEL] Spins left today: {remaining}/{cfg.perPlayer.maxSpinsPerDay}. Cooldown: {cooldown}s.");
        }
        catch (Exception ex)
        {
            SafeLog($"[HandleStatus] {ex}");
            SendPrivate(playerId, cfg.messages.error ?? "[WHEEL] Error.");
        }
    }

    // ================= Prize & Award =================
    private Prize PickRandomPrize()
    {
        var pool = cfg.rewardPool ?? new List<PrizeEntry>();
        if (pool.Count == 0)
        {
            // Never crash: fallback
            return new Prize { type = "item", itemId = 4119, amount = 1, label = "Mystery Item" };
        }
        var idx = rng.Next(pool.Count);
        var e = pool[idx];

        if (string.Equals(e.type, "item", StringComparison.OrdinalIgnoreCase))
        {
            return new Prize { type = "item", itemId = e.itemId, amount = e.amount, label = e.label ?? $"Item {e.itemId} x{e.amount}" };
        }
        else if (string.Equals(e.type, "neardeath", StringComparison.OrdinalIgnoreCase))
        {
            return new Prize { type = "neardeath", amount = 1, label = e.label ?? "Near Death Experience!" };
        }
        else
        {
            // Default fallback to item
            return new Prize { type = "item", itemId = e.itemId > 0 ? e.itemId : 4119, amount = e.amount > 0 ? e.amount : 1, label = e.label ?? "Unknown Prize" };
        }
    }

    private bool AwardPrize(int playerId, Prize prize)
    {
        try
        {
            if (prize == null) return false;

            if (string.Equals(prize.type, "item", StringComparison.OrdinalIgnoreCase))
            {
                if (prize.amount <= 0 || prize.itemId <= 0) return false;
                
                try
                {
                    SafeLog($"[AwardPrize] Attempting to award item {prize.itemId} x{prize.amount} to player {playerId}");
                    
                    // Use direct API call to add item to player inventory
                    var itemData = new IdItemStack
                    {
                        id = playerId,
                        itemStack = new ItemStack
                        {
                            id = prize.itemId,
                            count = prize.amount,
                            slotIdx = 0,
                            ammo = 0,
                            decay = 0
                        }
                    };
                    
                    api1.Game_Request(CmdId.Request_Player_AddItem, (ushort)DateTime.UtcNow.Millisecond, itemData);
                    SafeLog($"[AwardPrize] Item API call executed: ItemId={prize.itemId}, Amount={prize.amount}, PlayerId={playerId}");
                    
                    return true;
                }
                catch (Exception ex)
                {
                    SafeLog($"[AwardPrize] Item failed: {ex.Message}");
                    return false;
                }
            }
            else if (string.Equals(prize.type, "neardeath", StringComparison.OrdinalIgnoreCase))
            {
                // Drop player to 1 HP using PlayerInfoSet API call
                try
                {
                    var playerInfoSet = new PlayerInfoSet
                    {
                        entityId = playerId,
                        health = 1 // Drop to 1 HP for dramatic effect
                    };
                    api1.Game_Request(CmdId.Request_Player_SetPlayerInfo, (ushort)DateTime.UtcNow.Millisecond, playerInfoSet);
                    SafeLog($"[AwardPrize] Near Death: Dropped player {playerId} to 1 HP");
                    return true;
                }
                catch (Exception ex)
                {
                    SafeLog($"[AwardPrize] Near Death failed: {ex.Message}");
                    return false;
                }
            }

            return false;
        }
        catch (Exception ex)
        {
            SafeLog($"[AwardPrize] {ex}");
            return false;
        }
    }

    private static string PrizeToText(Prize p)
    {
        if (p == null) return "prize";
        if (string.Equals(p.type, "item", StringComparison.OrdinalIgnoreCase))
            return $"Item {p.itemId} x{p.amount}";
        if (string.Equals(p.type, "neardeath", StringComparison.OrdinalIgnoreCase))
            return "Near Death Experience!";
        return "prize";
    }

    // ================= Player data =================
    private PlayerProfile LoadOrCreateProfile(int pid)
    {
        lock (ioLock)
        {
            var path = GetProfilePath(pid);
            if (!File.Exists(path))
            {
                var p = new PlayerProfile { dayKey = TodayKey(), spinsToday = 0, lastSpinUtc = null };
                File.WriteAllText(path, JsonConvert.SerializeObject(p, Formatting.Indented));
                return p;
            }

            try
            {
                var raw = File.ReadAllText(path);
                var prof = JsonConvert.DeserializeObject<PlayerProfile>(raw) ?? new PlayerProfile();
                if (string.IsNullOrEmpty(prof.dayKey)) prof.dayKey = TodayKey();
                return prof;
            }
            catch
            {
                // corrupt -> reset
                var p = new PlayerProfile { dayKey = TodayKey(), spinsToday = 0, lastSpinUtc = null };
                File.WriteAllText(path, JsonConvert.SerializeObject(p, Formatting.Indented));
                return p;
            }
        }
    }

    private void SaveProfile(int pid, PlayerProfile prof)
    {
        lock (ioLock)
        {
            var path = GetProfilePath(pid);
            File.WriteAllText(path, JsonConvert.SerializeObject(prof, Formatting.Indented));
        }
    }

    private void ResetIfNewDay(PlayerProfile prof)
    {
        var today = TodayKey();
        if (!string.Equals(prof.dayKey, today, StringComparison.Ordinal))
        {
            prof.dayKey = today;
            prof.spinsToday = 0;
            // keep lastSpinUtc for info; cooldown is still evaluated
        }
    }

    private string GetProfilePath(int pid) => Path.Combine(stateDir, $"{pid}.json");
    private static string TodayKey() => DateTime.UtcNow.ToString("yyyy-MM-dd");

    // ================= Messaging =================
    private void Broadcast(string text)
    {
        try
        {
            var msg = new MessageData
            {
                Channel = Eleon.MsgChannel.Global,
                Text = text,
                SenderNameOverride = "Wheel"
            };
            api2.Application.SendChatMessage(msg);
        }
        catch (Exception ex) { SafeLog($"[Broadcast] {ex}"); }
    }

    private void SendPrivate(int playerId, string text)
    {
        try
        {
            var msg = new MessageData
            {
                Channel = Eleon.MsgChannel.SinglePlayer,
                RecipientEntityId = playerId,
                Text = text,
                SenderNameOverride = "Wheel"
            };
            api2.Application.SendChatMessage(msg);
        }
        catch (Exception ex) { SafeLog($"[SendPrivate] {ex}"); }
    }

    private string ResolveDisplayName(int playerId)
    {
        // Check cache first for instant response
        if (playerNameCache.TryGetValue(playerId, out string cachedName))
        {
            SafeLog($"[ResolveDisplayName] Using cached name for {playerId}: {cachedName}");
            return cachedName;
        }
        
        // Avoid duplicate lookups
        if (pendingPlayerLookups.ContainsKey(playerId))
        {
            SafeLog($"[ResolveDisplayName] Player lookup already pending for {playerId}");
            return $"Player_{playerId}";
        }
        
        // Request player info asynchronously
        Task.Run(async () => await RequestPlayerInfoAsync(playerId));
        
        // Return fallback name while lookup is pending
        return $"Player_{playerId}";
    }
    
    private async Task<string> RequestPlayerInfoAsync(int playerId)
    {
        try
        {
            // Mark as pending to avoid duplicate requests
            pendingPlayerLookups[playerId] = DateTime.UtcNow;
            
            // Request player info from game API
            api1.Game_Request(CmdId.Request_Player_Info, (ushort)DateTime.Now.Millisecond, new Id(playerId));
            
            // Wait a short time for response
            await Task.Delay(500);
            
            // Check if we received the player info
            if (playerNameCache.TryGetValue(playerId, out string name))
            {
                SafeLog($"[RequestPlayerInfoAsync] SUCCESS: Resolved player {playerId} to name: {name}");
                return name;
            }
            
            SafeLog($"[RequestPlayerInfoAsync] No response received for player {playerId}");
            return null;
        }
        catch (Exception ex)
        {
            SafeLog($"[RequestPlayerInfoAsync] Error requesting player info for {playerId}: {ex.Message}");
            return null;
        }
        finally
        {
            // Remove from pending lookups
            pendingPlayerLookups.TryRemove(playerId, out _);
        }
    }
    
    private void CachePlayerName(int playerId, string playerName)
    {
        if (playerId > 0 && !string.IsNullOrEmpty(playerName) && playerName != "Unknown Player")
        {
            playerNameCache[playerId] = playerName;
            SafeLog($"[CachePlayerName] Cached player name {playerId} -> {playerName}");
        }
    }

    private void LoadConfig()
    {
        try
        {
            if (!File.Exists(cfgPath))
            {
                cfg = WheelConfig.Default();
                File.WriteAllText(cfgPath, JsonConvert.SerializeObject(cfg, Formatting.Indented));
                SafeLog($"[LoadConfig] Created default {cfgPath}");
            }
            else
            {
                cfg = JsonConvert.DeserializeObject<WheelConfig>(File.ReadAllText(cfgPath)) ?? WheelConfig.Default();
                SafeLog($"[LoadConfig] Loaded {cfgPath}");
            }

            // Validate configuration after loading
            if (!ValidatePrizePool())
            {
                SafeLog("[LoadConfig] WARNING: Prize pool has fewer than 3 items. Wheel needs at least 3 prizes to work properly!");
            }
        }
        catch (Exception ex)
        {
            SafeLog($"[LoadConfig] {ex} - using defaults.");
            cfg = WheelConfig.Default();
        }
    }

    private void SafeLog(string line)
    {
        try { File.AppendAllText(logPath, $"{DateTime.UtcNow:yyyy-MM-dd HH:mm:ss} {line}\n"); }
        catch { }
    }

    private bool ValidatePrizePool()
    {
        var pool = cfg?.rewardPool;
        return pool != null && pool.Count >= 3;
    }

    // ================= Models =================
    private class WheelConfig
    {
        public PerPlayer perPlayer { get; set; } = new PerPlayer();
        public List<PrizeEntry> rewardPool { get; set; } = new List<PrizeEntry>();
        public Messages messages { get; set; } = Messages.Default();

        public static WheelConfig Default() => new WheelConfig
        {
            perPlayer = new PerPlayer { maxSpinsPerDay = 3, cooldownSeconds = 300 },
            rewardPool = new List<PrizeEntry>
            {
                new PrizeEntry { type = "item", amount = 1, itemId = 7901, label = "Salvage Token" },
                new PrizeEntry { type = "item", amount = 10, itemId = 7901, label = "Salvage Token" },
                new PrizeEntry { type = "item", amount = 100, itemId = 7901, label = "Salvage Token" },
                new PrizeEntry { type = "item", amount = 1000, itemId = 4344, label = "Credits" },
                new PrizeEntry { type = "item", amount = 10000, itemId = 4344, label = "Credits" },
                new PrizeEntry { type = "item", amount = 100000, itemId = 4344, label = "Credits" },
                new PrizeEntry { type = "neardeath", amount = 1, itemId = 0, label = "Near Death Experience!" }
            },
            messages = Messages.Default(),
        };
    }

    private class PerPlayer { public int maxSpinsPerDay { get; set; } = 3; public int cooldownSeconds { get; set; } = 300; }


    private class Messages
    {
        public string spinStart { get; set; }
        public string won { get; set; }
        public string cooldown { get; set; }
        public string limit { get; set; }
        public string error { get; set; }
        public string adminError { get; set; }

        public static Messages Default() => new Messages
        {
            spinStart = "[WHEEL] Spinning the wheel for {player}…",
            won       = "[WHEEL] {player} won {reward}!",
            cooldown  = "[WHEEL] Cooldown active. Try again in {secs}s.",
            limit     = "[WHEEL] You already used all {count} spins for today.",
            error     = "[WHEEL] Something went wrong. Contact admin.",
            adminError = "[WHEEL] Hey admin, your wheel is broken! Need at least 3 prizes. What kind of wheel has 0-2 options? That's not a wheel, that's a coin flip!"
        };
    }

    private class PrizeEntry
    {
        public string type { get; set; } = "item"; // "item" or "neardeath"
        public int amount { get; set; } = 0;
        public int itemId { get; set; } = 0;          // for type=item
        public string label { get; set; }
    }

    private class Prize
    {
        public string type;
        public int amount;
        public int itemId;
        public string label;
    }

    private class PlayerProfile
    {
        public string dayKey { get; set; }          // "yyyy-MM-dd" (UTC)
        public int spinsToday { get; set; }         // spins used today
        public DateTime? lastSpinUtc { get; set; }  // last spin timestamp (UTC)
    }
}
