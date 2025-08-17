using Eleon;
using Eleon.Modding;
using System;
using System.Collections.Generic;
using System.Linq;

public class DeathMessagesMod : ModInterface, IMod
{
    private ModGameAPI api;
    private IModApi api2;
    private Random random = new Random();

    // Death messages organized by actual death cause codes
    private Dictionary<int, string[]> deathMessagesByType = new Dictionary<int, string[]>
    {
        [0] = new[] { // Unknown
            "{player} died mysteriously",
            "{player} experienced an unknown fate",
            "{player} vanished from existence",
            "{player} encountered something beyond comprehension"
        },
        [1] = new[] { // Projectile
            "{player} got shot down",
            "{player} became target practice",
            "{player} caught a bullet with their face",
            "{player} learned that dodging is important",
            "{player} discovered why cover exists",
            "{player} became Swiss cheese"
        },
        [2] = new[] { // Explosion
            "{player} went out with a bang",
            "{player} discovered that grenades have no safety switch",
            "{player} became unplanned participant in an explosion",
            "{player} found out what the big red button does",
            "{player} experienced rapid unscheduled disassembly",
            "{player} learned why you don't stand near explosive barrels"
        },
        [3] = new[] { // Food/Starvation
            "{player} forgot that food is important",
            "{player} discovered the hard way that you can't eat metal",
            "{player} should have packed more emergency rations",
            "{player} learned that hunger is not just a suggestion",
            "{player} decided fasting was a bad idea"
        },
        [4] = new[] { // Oxygen
            "{player} forgot to breathe",
            "{player} discovered that space is not very user-friendly",
            "{player} found out oxygen is actually quite important",
            "{player} learned that holding your breath doesn't work in space",
            "{player} discovered why spacesuits exist"
        },
        [5] = new[] { // Disease
            "{player} should have washed their hands",
            "{player} caught something nasty",
            "{player} discovered that alien germs are unfriendly",
            "{player} learned why medical supplies exist"
        },
        [6] = new[] { // Drowning
            "{player} forgot they weren't a fish",
            "{player} went for an unplanned swim",
            "{player} discovered water breathing isn't a thing",
            "{player} learned that gills are optional equipment",
            "{player} found out swimming lessons matter"
        },
        [7] = new[] { // Fall
            "{player} decided to test gravity... gravity won",
            "{player} forgot to pack a parachute",
            "{player} achieved maximum velocity into the ground",
            "{player} discovered that falling damage is a thing",
            "{player} learned physics the hard way",
            "{player} thought they could fly. They were wrong."
        },
        [8] = new[] { // Suicide
            "{player} rage-quit life",
            "{player} took the easy way out",
            "{player} decided to reset their character",
            "{player} pressed the wrong button"
        }
    };

    // IModApi implementation for hybrid API1+API2 approach
    public void Init(IModApi modApi)
    {
        api2 = modApi;
    }

    public void Game_Start(ModGameAPI dediAPI)
    {
        api = dediAPI;
        int totalMessages = deathMessagesByType.Values.Sum(msgs => msgs.Length);
        api.Console_Write($"DeathMessagesMod loaded with {totalMessages} context-aware death messages across {deathMessagesByType.Count} death causes!");
        api.Console_Write("DeathMessagesMod: Using hybrid API1+API2 for clean messaging with real death cause detection");
    }

    public void Game_Exit()
    {
        api?.Console_Write("DeathMessagesMod shutting down.");
    }

    // IMod interface implementation
    public void Shutdown()
    {
        api?.Console_Write("DeathMessagesMod: IMod Shutdown called");
    }

    public void Game_Update() { }

    public void Game_Event(CmdId eventId, ushort seqNr, object data)
    {
        // Listen for statistics events (includes player deaths)
        if (eventId == CmdId.Event_Statistics)
        {
            api.Console_Write("[DEBUG] Statistics event triggered");
            
            try
            {
                if (data is StatisticsParam statsParam)
                {
                    api.Console_Write($"[DEBUG] Statistics type: {statsParam.type} (was previously: {statsParam.type.ToString()})");
                    
                    // Check if this is a player death event
                    if (statsParam.type == StatisticsType.PlayerDied)
                    {
                        api.Console_Write($"[DEBUG] Player died! EntityID: {statsParam.int1}, Death cause: {statsParam.int2}");
                        
                        // Store death cause info temporarily (we'll use a simple approach with seqNr)
                        int deathCause = statsParam.int2;
                        
                        // statsParam.int1 contains the EntityID of the player who died
                        Id playerId = new Id() { id = statsParam.int1 };
                        
                        // Request player info to get the name - encode death cause in seqNr for retrieval
                        ushort encodedSeqNr = (ushort)(2000 + Math.Max(0, Math.Min(deathCause, 99))); // 2000-2099 range
                        api.Game_Request(CmdId.Request_Player_Info, encodedSeqNr, playerId);
                        return;
                    }
                }
                else
                {
                    api.Console_Write($"[DEBUG] Unexpected statistics data type: {data?.GetType()?.FullName}");
                }
            }
            catch (Exception ex)
            {
                api.Console_Write($"[ERROR] Failed to process statistics event: {ex.Message}");
            }
        }
        else if (eventId == CmdId.Event_Player_Info && seqNr >= 2000 && seqNr <= 2099)
        {
            // This is the response to our PlayerInfo request for death - decode death cause
            int deathCause = seqNr - 2000;
            api.Console_Write($"[DEBUG] Received PlayerInfo response for death event, cause: {deathCause}");
            
            try
            {
                if (data is PlayerInfo playerInfo)
                {
                    string playerName = playerInfo.playerName ?? "Unknown Player";
                    SendDeathMessage(playerName, deathCause);
                }
            }
            catch (Exception ex)
            {
                api.Console_Write($"[ERROR] Failed to process PlayerInfo response for death: {ex.Message}");
            }
        }
    }

    private void SendDeathMessage(string playerName, int deathCause)
    {
        try
        {
            // Get appropriate messages for the death cause
            string[] messages = deathMessagesByType.ContainsKey(deathCause) 
                ? deathMessagesByType[deathCause] 
                : deathMessagesByType[0]; // fallback to unknown
                
            // Pick a random message from the appropriate category
            string messageTemplate = messages[random.Next(messages.Length)];
            string baseMessage = messageTemplate.Replace("{player}", playerName);
            
            // Add brackets to make it clear this is a server message
            string message = $"[{baseMessage}]";
            
            // Enhanced logging with death cause info
            string[] causeNames = {"Unknown", "Projectile", "Explosion", "Starvation", "Oxygen", "Disease", "Drowning", "Fall", "Suicide"};
            string causeName = deathCause < causeNames.Length ? causeNames[deathCause] : "Unknown";
            api.Console_Write($"[DEATH] {playerName} died from {causeName} (code {deathCause}): {baseMessage}");
            
            // Use API2 for clean messaging (eliminates duplicates)
            if (api2?.Application != null)
            {
                var messageData = new Eleon.MessageData
                {
                    Channel = Eleon.MsgChannel.Global,
                    Text = message,
                    SenderNameOverride = "Server"
                };

                api2.Application.SendChatMessage(messageData);
                api.Console_Write($"[DEATH] Sent API2 message: {message}");
            }
            else
            {
                // Fallback to API1 if API2 not available
                string command = "SAY '" + message + "'";
                api.Game_Request(CmdId.Request_ConsoleCommand, (ushort)CmdId.Request_InGameMessage_AllPlayers, 
                                new PString(command));
                api.Console_Write($"[DEATH] Sent API1 fallback message: {message}");
            }
        }
        catch (Exception ex)
        {
            api.Console_Write($"[ERROR] Failed to send death message: {ex.Message}");
        }
    }
}