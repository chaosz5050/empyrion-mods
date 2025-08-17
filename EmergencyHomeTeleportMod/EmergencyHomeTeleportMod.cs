using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using Newtonsoft.Json;
using Eleon.Modding;
using Eleon;

namespace EmergencyHomeTeleport
{
    public class EmergencyHomeTeleportMod : IMod, ModInterface
    {
        private IModApi api2;
        private ModGameAPI api1;
        
        private string ModDir;
        private string HomesPath;
        private string ConfigPath;
        
        private Dictionary<int, PlayerHomeData> Homes = new Dictionary<int, PlayerHomeData>();
        private ModConfig Config = new ModConfig { MaxTeleportsPer24h = 3 };
        private readonly Dictionary<int, DateTime> PendingConfirmUntil = new Dictionary<int, DateTime>();
        private readonly Dictionary<int, PlayerInfo> PendingPlayerInfoRequests = new Dictionary<int, PlayerInfo>();

        // API2 Implementation
        public void Init(IModApi api)
        {
            this.api2 = api;
            var modDirectory = Path.GetDirectoryName(System.Reflection.Assembly.GetExecutingAssembly().Location);
            ModDir = Path.Combine(modDirectory, "EmergencyHomeTeleport");
            HomesPath = Path.Combine(ModDir, "homes.json");
            ConfigPath = Path.Combine(ModDir, "config.json");
            
            Directory.CreateDirectory(ModDir);
            LoadConfig();
            LoadHomes();
            
            Log($"[EmergencyHomeTeleport] API2 Ready. MaxTeleportsPer24h={Config.MaxTeleportsPer24h}");
        }

        public void Shutdown()
        {
            Log("[EmergencyHomeTeleport] Shutdown");
        }

        // API1 Implementation 
        public void Game_Start(ModGameAPI dediAPI)
        {
            api1 = dediAPI;
            Log("[EmergencyHomeTeleport] API1 Initialized - ready for commands");
        }

        public void Game_Exit()
        {
            Log("[EmergencyHomeTeleport] API1 shutting down");
        }

        public void Game_Update() { }

        public void Game_Event(CmdId eventId, ushort seqNr, object data)
        {
            try
            {
                if (eventId == CmdId.Event_ChatMessage)
                {
                    var chatInfo = (ChatInfo)data;
                    if (chatInfo == null || string.IsNullOrEmpty(chatInfo.msg)) return;

                    var msg = chatInfo.msg.Trim();
                    if (!msg.StartsWith("/")) return;

                    // Handle commands asynchronously
                    Task.Run(async () =>
                    {
                        try
                        {
                            if (msg == "/sethome")
                                await HandleSetHome(chatInfo);
                            else if (msg == "/home uses")
                                HandleShowUses(chatInfo);
                            else if (msg == "/home")
                                HandleHomeBegin(chatInfo);
                        }
                        catch (Exception ex)
                        {
                            Log($"[EmergencyHomeTeleport] Command error: {ex.Message}");
                        }
                    });
                }
                else if (eventId == CmdId.Event_Player_Info)
                {
                    // Handle player info response
                    if (data is PlayerInfo playerInfo)
                    {
                        PendingPlayerInfoRequests[playerInfo.entityId] = playerInfo;
                        Log($"[EmergencyHomeTeleport] Received player info for {playerInfo.entityId} at {playerInfo.playfield} @ {FormatVector(playerInfo.pos)}");
                    }
                }
                else if (eventId == CmdId.Event_DialogButtonIndex)
                {
                    // Handle dialog button responses
                    HandleDialogResponse(data);
                }
            }
            catch (Exception ex)
            {
                Log($"[EmergencyHomeTeleport] Event error: {ex.Message}");
            }
        }

        private async Task HandleSetHome(ChatInfo chatInfo)
        {
            try
            {
                var entityId = chatInfo.playerId;
                
                // Clear any previous request
                PendingPlayerInfoRequests.Remove(entityId);
                
                // Request current player info
                api1.Game_Request(CmdId.Request_Player_Info, (ushort)DateTime.Now.Millisecond, new Id(entityId));
                Log($"[EmergencyHomeTeleport] Requested player info for {entityId}");
                
                // Wait for response (check multiple times)
                for (int i = 0; i < 10; i++)
                {
                    await Task.Delay(100);
                    if (PendingPlayerInfoRequests.TryGetValue(entityId, out var playerInfo))
                    {
                        // Got the player info! Set the home location with safety height offset
                        var rec = GetOrCreate(entityId);
                        var safePosition = new PVector3(playerInfo.pos.x, playerInfo.pos.y + 3.0f, playerInfo.pos.z); // Add 3m height for safety
                        rec.Home = new HomePoint
                        {
                            Playfield = playerInfo.playfield,
                            Position = safePosition,
                            Rotation = playerInfo.rot
                        };
                        rec.SetHomeRequested = false;
                        SaveHomes();
                        
                        InformPlayer(entityId, $"Home set at: {rec.Home.Playfield} @ {FormatVector(rec.Home.Position)}");
                        Log($"[EmergencyHomeTeleport] Player {entityId} set home at {rec.Home.Playfield} @ {FormatVector(rec.Home.Position)}");
                        
                        // Clean up
                        PendingPlayerInfoRequests.Remove(entityId);
                        return;
                    }
                }
                
                // If we get here, we didn't get a response
                InformPlayer(entityId, "Failed to get your current position. Try /sethome again.");
                Log($"[EmergencyHomeTeleport] Failed to get player info response for {entityId}");
            }
            catch (Exception ex)
            {
                Log($"[EmergencyHomeTeleport] SetHome error: {ex.Message}");
            }
        }

        private void HandleShowUses(ChatInfo chatInfo)
        {
            try
            {
                var entityId = chatInfo.playerId;
                var rec = GetOrCreate(entityId);
                Prune(rec);
                var left = Math.Max(0, Config.MaxTeleportsPer24h - rec.Uses.Count);
                
                InformPlayer(entityId, $"Uses left (24h): {left}/{Config.MaxTeleportsPer24h}");
            }
            catch (Exception ex)
            {
                Log($"[EmergencyHomeTeleport] ShowUses error: {ex.Message}");
            }
        }

        private void HandleHomeBegin(ChatInfo chatInfo)
        {
            try
            {
                var entityId = chatInfo.playerId;
                var rec = GetOrCreate(entityId);

                if (rec.Home == null)
                {
                    InformPlayer(entityId, "No /sethome found. Use /sethome first.");
                    return;
                }

                Prune(rec);
                if (rec.Uses.Count >= Config.MaxTeleportsPer24h)
                {
                    var oldest = rec.Uses.Min();
                    var until = oldest.AddHours(24) - DateTime.UtcNow;
                    InformPlayer(entityId, $"Daily /home limit reached ({Config.MaxTeleportsPer24h}/24h). Resets in ~{FormatTimeSpan(until)}.");
                    return;
                }

                // Show confirmation
                ShowTeleportConfirmation(entityId, rec);
            }
            catch (Exception ex)
            {
                Log($"[EmergencyHomeTeleport] HomeBegin error: {ex.Message}");
            }
        }

        private void HandleDialogResponse(object data)
        {
            try
            {
                // Try to extract dialog response data
                var dataType = data?.GetType();
                Log($"[EmergencyHomeTeleport] Dialog response data type: {dataType?.Name}");
                
                // Handle IdAndIntValue response type specifically
                if (data is IdAndIntValue idAndInt)
                {
                    // Use reflection to get the correct field names for IdAndIntValue
                    var idFields = typeof(IdAndIntValue).GetFields().Where(f => f.FieldType == typeof(int)).ToArray();
                    Log($"[EmergencyHomeTeleport] IdAndIntValue fields: {string.Join(", ", idFields.Select(f => f.Name))}");
                    
                    if (idFields.Length >= 2)
                    {
                        var entityId = (int)idFields[0].GetValue(idAndInt); // First int field should be Id
                        var buttonIndex = (int)idFields[1].GetValue(idAndInt); // Second int field should be Value/ButtonIndex
                        
                        Log($"[EmergencyHomeTeleport] Dialog response: Id={entityId}, ButtonIndex={buttonIndex}");
                        
                        // Check if this is a valid pending teleport
                        if (!PendingConfirmUntil.TryGetValue(entityId, out var until) || DateTime.UtcNow > until)
                        {
                            Log($"[EmergencyHomeTeleport] No valid pending teleport for {entityId}");
                            InformPlayer(entityId, "No pending teleport or it expired. Use /home again.");
                            PendingConfirmUntil.Remove(entityId);
                            return;
                        }
                        
                        PendingConfirmUntil.Remove(entityId);
                        
                        Log($"[EmergencyHomeTeleport] Dialog button {buttonIndex} clicked by player {entityId}");
                        
                        if (buttonIndex == 0) // Positive button = "Teleport Now"
                        {
                            Log($"[EmergencyHomeTeleport] Player {entityId} clicked TELEPORT NOW button");
                            Task.Run(async () => await ExecuteTeleport(entityId));
                        }
                        else if (buttonIndex == 1) // Negative button = "Cancel"
                        {
                            Log($"[EmergencyHomeTeleport] Player {entityId} clicked CANCEL button");
                            InformPlayer(entityId, "Emergency teleport cancelled.");
                        }
                        else
                        {
                            Log($"[EmergencyHomeTeleport] Player {entityId} clicked unknown button {buttonIndex} - treating as cancel");
                            InformPlayer(entityId, "Emergency teleport cancelled.");
                        }
                    }
                    else
                    {
                        Log($"[EmergencyHomeTeleport] Could not find Id or Value properties in IdAndIntValue");
                    }
                }
                else
                {
                    // Fallback for other response types
                    Log($"[EmergencyHomeTeleport] Unexpected dialog response type: {dataType?.Name}");
                    
                    // Debug: List all properties of the response data for other types
                    if (data != null)
                    {
                        var responseProps = dataType.GetProperties();
                        Log($"[EmergencyHomeTeleport] Dialog response properties: {string.Join(", ", responseProps.Select(p => $"{p.Name}={p.GetValue(data)?.ToString()}"))}");
                    }
                }
            }
            catch (Exception ex)
            {
                Log($"[EmergencyHomeTeleport] Dialog response error: {ex.Message}");
            }
        }

        private void ShowTeleportConfirmation(int entityId, PlayerHomeData rec)
        {
            try
            {
                PendingConfirmUntil[entityId] = DateTime.UtcNow.AddMinutes(2);
                
                var usesLeft = Config.MaxTeleportsPer24h - rec.Uses.Count;
                var message = $"Emergency Teleport Confirmation\n\n" +
                             $"You will be teleported to:\n" +
                             $"{rec.Home.Playfield} @ {FormatVector(rec.Home.Position)}\n\n" +
                             $"Your current vehicle will come with you!\n" +
                             $"Uses left today: {usesLeft}/{Config.MaxTeleportsPer24h}";

                // Create dialog box with Yes/Cancel buttons
                var dialogData = new DialogBoxData();
                dialogData.Id = entityId;
                dialogData.MsgText = message;
                
                // Set the two-button dialog using the discovered fields!
                try
                {
                    var posButtonField = typeof(DialogBoxData).GetField("PosButtonText");
                    var negButtonField = typeof(DialogBoxData).GetField("NegButtonText");
                    
                    if (posButtonField != null && negButtonField != null)
                    {
                        posButtonField.SetValue(dialogData, "✅ Teleport Now");
                        negButtonField.SetValue(dialogData, "❌ Cancel");
                        Log($"[EmergencyHomeTeleport] SUCCESS: Set two-button dialog - Teleport Now / Cancel");
                    }
                    else
                    {
                        Log($"[EmergencyHomeTeleport] ERROR: Could not find PosButtonText or NegButtonText fields");
                    }
                }
                catch (Exception ex)
                {
                    Log($"[EmergencyHomeTeleport] ERROR setting button texts: {ex.Message}");
                }

                api1.Game_Request(CmdId.Request_ShowDialog_SinglePlayer, (ushort)DateTime.Now.Millisecond, dialogData);
                Log($"[EmergencyHomeTeleport] Showed dialog confirmation to player {entityId}");
            }
            catch (Exception ex)
            {
                Log($"[EmergencyHomeTeleport] Dialog error: {ex.Message}");
                InformPlayer(entityId, "Error showing confirmation dialog. Try again.");
            }
        }

        private async Task ExecuteTeleport(int entityId)
        {
            try
            {
                var rec = GetOrCreate(entityId);
                if (rec.Home == null)
                {
                    InformPlayer(entityId, "No /sethome found. Use /sethome first.");
                    return;
                }

                Prune(rec);
                if (rec.Uses.Count >= Config.MaxTeleportsPer24h)
                {
                    var oldest = rec.Uses.Min();
                    var until = oldest.AddHours(24) - DateTime.UtcNow;
                    InformPlayer(entityId, $"Daily /home limit reached ({Config.MaxTeleportsPer24h}/24h). Resets in ~{FormatTimeSpan(until)}.");
                    return;
                }

                // Get current player location to determine teleport method
                PendingPlayerInfoRequests.Remove(entityId);
                api1.Game_Request(CmdId.Request_Player_Info, (ushort)DateTime.Now.Millisecond, new Id(entityId));
                await Task.Delay(200); // Wait for current position

                string currentPlayfield = "Unknown";
                if (PendingPlayerInfoRequests.TryGetValue(entityId, out var currentPlayerInfo))
                {
                    currentPlayfield = currentPlayerInfo.playfield;
                    PendingPlayerInfoRequests.Remove(entityId);
                }

                // Teleportation with MUCH higher safety height
                var destPos = new PVector3(rec.Home.Position.x, rec.Home.Position.y + 2.0f, rec.Home.Position.z);
                var destRot = rec.Home.Rotation;

                Log($"[EmergencyHomeTeleport] Current: {currentPlayfield}, Destination: {rec.Home.Playfield}");

                if (currentPlayfield != rec.Home.Playfield)
                {
                    // Cross-playfield teleport
                    try
                    {
                        await RequestTeleport(CmdId.Request_Player_ChangePlayerfield, new IdPlayfieldPositionRotation
                        {
                            id = entityId,
                            playfield = rec.Home.Playfield,
                            pos = destPos,
                            rot = destRot
                        });
                        Log($"[EmergencyHomeTeleport] Cross-playfield teleport: {entityId} from {currentPlayfield} to {rec.Home.Playfield}");
                    }
                    catch (Exception ex)
                    {
                        throw new Exception($"Cross-playfield teleport failed: {ex.Message}");
                    }
                }
                else
                {
                    // Same-playfield teleport
                    try
                    {
                        await RequestTeleport(CmdId.Request_Entity_Teleport, new IdPositionRotation
                        {
                            id = entityId,
                            pos = destPos,
                            rot = destRot
                        });
                        Log($"[EmergencyHomeTeleport] Same-playfield teleport: {entityId} within {currentPlayfield}");
                    }
                    catch (Exception ex)
                    {
                        throw new Exception($"Same-playfield teleport failed: {ex.Message}");
                    }
                }

                // Wait a moment for teleportation to complete
                await Task.Delay(500);

                // Health restoration AFTER teleportation - restore to max health
                try 
                { 
                    int maxHealth = 100; // Default fallback
                    if (currentPlayerInfo != null)
                    {
                        maxHealth = (int)currentPlayerInfo.healthMax;
                        Log($"[EmergencyHomeTeleport] Player max health: {maxHealth}");
                    }
                    
                    await RequestTeleport(CmdId.Request_Player_SetPlayerInfo, new PlayerInfoSet 
                    { 
                        entityId = entityId, 
                        health = maxHealth // Set to actual max health
                    }); 
                    
                    Log($"[EmergencyHomeTeleport] Health restored to {maxHealth} after teleportation");
                }
                catch (Exception e) 
                { 
                    Log($"[EmergencyHomeTeleport] SetHealth warning: {e.Message}"); 
                }
                
                rec.Uses.Add(DateTime.UtcNow);
                SaveHomes();

                InformPlayer(entityId, $"Teleported to /home: {rec.Home.Playfield} @ {FormatVector(rec.Home.Position)}");
                Log($"[EmergencyHomeTeleport] Player {entityId} successfully teleported to {rec.Home.Playfield}");
            }
            catch (Exception ex)
            {
                InformPlayer(entityId, $"Teleport failed: {ex.Message}");
                Log($"[EmergencyHomeTeleport] Teleport error for player {entityId}: {ex}");
            }
        }

        // Helper methods

        private async Task<PlayerInfo> GetPlayerInfoAsync(int playerId)
        {
            try
            {
                // Use simpler pattern like PlayerStatusMod - fire request and wait
                api1.Game_Request(CmdId.Request_Player_Info, (ushort)DateTime.Now.Millisecond, new Id(playerId));
                await Task.Delay(500); // Wait for response like PlayerStatusMod
                
                // For now, return null - in production this would need proper response handling
                // But we can use the chatInfo.playerId approach instead
                return null;
            }
            catch (Exception ex)
            {
                Log($"[EmergencyHomeTeleport] GetPlayerInfo error: {ex.Message}");
                return null;
            }
        }

        private async Task RequestTeleport(CmdId cmdId, object data)
        {
            try
            {
                api1.Game_Request(cmdId, (ushort)DateTime.Now.Millisecond, data);
                await Task.Delay(100); // Brief delay for request processing
            }
            catch (Exception ex)
            {
                Log($"[EmergencyHomeTeleport] Request {cmdId} error: {ex.Message}");
                throw;
            }
        }

        private void InformPlayer(int entityId, string message)
        {
            try
            {
                // Use API2 messaging system like PlayerStatusMod
                var messageData = new Eleon.MessageData
                {
                    Channel = Eleon.MsgChannel.SinglePlayer,
                    Text = message,
                    SenderNameOverride = "Emergency Teleport",
                    RecipientEntityId = entityId
                };
                api2.Application.SendChatMessage(messageData);
            }
            catch (Exception ex)
            {
                Log($"[EmergencyHomeTeleport] InformPlayer error: {ex.Message}");
            }
        }

        private PlayerHomeData GetOrCreate(int entityId)
        {
            if (!Homes.TryGetValue(entityId, out var rec))
            {
                rec = new PlayerHomeData();
                Homes[entityId] = rec;
            }
            return rec;
        }

        private void Prune(PlayerHomeData rec)
        {
            var cutoff = DateTime.UtcNow.AddHours(-24);
            rec.Uses = rec.Uses.Where(t => t >= cutoff).ToList();
        }

        private void LoadHomes()
        {
            try
            {
                if (!File.Exists(HomesPath)) 
                { 
                    Homes = new Dictionary<int, PlayerHomeData>(); 
                    return; 
                }
                var json = File.ReadAllText(HomesPath);
                Homes = JsonConvert.DeserializeObject<Dictionary<int, PlayerHomeData>>(json) ?? new Dictionary<int, PlayerHomeData>();
                Log($"[EmergencyHomeTeleport] Loaded {Homes.Count} player homes");
            }
            catch (Exception ex)
            { 
                Log($"[EmergencyHomeTeleport] Load homes error: {ex.Message}");
                Homes = new Dictionary<int, PlayerHomeData>(); 
            }
        }

        private void SaveHomes()
        {
            try
            {
                var json = JsonConvert.SerializeObject(Homes, Formatting.Indented);
                File.WriteAllText(HomesPath, json);
            }
            catch (Exception ex)
            {
                Log($"[EmergencyHomeTeleport] Save homes error: {ex.Message}");
            }
        }

        private void LoadConfig()
        {
            try
            {
                if (!File.Exists(ConfigPath))
                {
                    Config = new ModConfig { MaxTeleportsPer24h = 3 };
                    SaveConfig();
                    return;
                }
                var json = File.ReadAllText(ConfigPath);
                Config = JsonConvert.DeserializeObject<ModConfig>(json) ?? new ModConfig { MaxTeleportsPer24h = 3 };
                ValidateConfig();
                SaveConfig();
                Log($"[EmergencyHomeTeleport] Loaded config: {Config.MaxTeleportsPer24h} uses per 24h");
            }
            catch (Exception ex)
            {
                Log($"[EmergencyHomeTeleport] Load config error: {ex.Message}");
                Config = new ModConfig { MaxTeleportsPer24h = 3 };
                SaveConfig();
            }
        }

        private void SaveConfig()
        {
            try
            {
                var json = JsonConvert.SerializeObject(Config, Formatting.Indented);
                File.WriteAllText(ConfigPath, json);
            }
            catch (Exception ex)
            {
                Log($"[EmergencyHomeTeleport] Save config error: {ex.Message}");
            }
        }

        private void ValidateConfig()
        {
            if (Config.MaxTeleportsPer24h < 1) Config.MaxTeleportsPer24h = 1;
            if (Config.MaxTeleportsPer24h > 24) Config.MaxTeleportsPer24h = 24;
        }

        private void Log(string message)
        {
            Console.WriteLine($"[{DateTime.Now:yyyy-MM-dd HH:mm:ss}] {message}");
        }

        private static string FormatVector(PVector3 v) => $"{v.x:0.0},{v.y:0.0},{v.z:0.0}";
        
        private static string FormatTimeSpan(TimeSpan ts)
        {
            if (ts <= TimeSpan.Zero) return "0s";
            if (ts.TotalHours >= 1) return $"{(int)ts.TotalHours}h {(int)ts.Minutes}m";
            if (ts.TotalMinutes >= 1) return $"{(int)ts.TotalMinutes}m";
            return $"{(int)ts.TotalSeconds}s";
        }

        // Data classes
        private class PlayerHomeData
        {
            public HomePoint Home { get; set; }
            public List<DateTime> Uses { get; set; } = new List<DateTime>();
            public bool SetHomeRequested { get; set; } = false;
        }

        private class HomePoint
        {
            public string Playfield { get; set; }
            public PVector3 Position { get; set; }
            public PVector3 Rotation { get; set; }
        }

        private class ModConfig
        {
            public int MaxTeleportsPer24h { get; set; } = 3;
        }
    }
}