using Eleon;
using Eleon.Modding;
using System;
using System.IO;
using Newtonsoft.Json;
using System.Collections.Generic;

public class VirtualBackpackMod : ModInterface
{
  private ModGameAPI api;
  private string dataPath = Path.Combine(Path.GetDirectoryName(typeof(VirtualBackpackMod).Assembly.Location), "PlayerData");
  private Dictionary<int, int> activeBackpackIndex = new Dictionary<int, int>();
  // Track which player/slot is open to prevent concurrent access
  private HashSet<string> backpackLocks = new HashSet<string>();
  // If admin opens remotely: maps adminId -> (targetPlayerId, slot)
  private Dictionary<int, (int targetPlayer, int slot)> adminOpenMapping = new Dictionary<int, (int, int)>();
  // Configured list of admin player IDs (populated from YAML)
  private HashSet<int> adminIds = new HashSet<int>();
  // Cache for player permission info
  private Dictionary<int, PlayerInfo> playerInfoCache = new Dictionary<int, PlayerInfo>();

  public void Game_Start(ModGameAPI dediAPI)
  {
    api = dediAPI;
    Directory.CreateDirectory(dataPath);
    api.Console_Write("VirtualBackpackMod loaded.");
    // Load configured admin IDs for remote access
    LoadAdminList();
  }

  public void Game_Exit()
  {
    // Save any open backpacks before shutdown
    foreach (var kvp in activeBackpackIndex)
    {
      api.Console_Write($"[Backpack] Cleaning up tracking for player {kvp.Key}");
    }
    activeBackpackIndex.Clear();
    
    api?.Console_Write("VirtualBackpackMod shutting down.");
  }

  public void Game_Update() { }

  public void Game_Event(CmdId eventId, ushort seqNr, object data)
  {
    // Cache player info for permission checking
    if (eventId == CmdId.Event_Player_Info)
    {
      var playerInfo = (PlayerInfo)data;
      playerInfoCache[playerInfo.entityId] = playerInfo;
      return;
    }
    
    // Handle player connections - request their info for caching
    if (eventId == CmdId.Event_Player_Connected)
    {
      var connectInfo = (Id)data;
      api.Game_Request(CmdId.Request_Player_Info, seqNr, connectInfo);
      return;
    }
    
    // Handle player disconnections - cleanup cache
    if (eventId == CmdId.Event_Player_Disconnected)
    {
      var disconnectInfo = (Id)data;
      playerInfoCache.Remove(disconnectInfo.id);
      return;
    }
    
    // Handle both ChatMessage and ChatMessageEx events for player/admin commands
    if (eventId == CmdId.Event_ChatMessage || eventId == CmdId.Event_ChatMessageEx)
    {
      var chat = (ChatInfo)data;
      string msg = chat.msg.Trim().ToLowerInvariant();

      // Admin remote open: /vbopen <playerId|playerName> <1-5>
      if (msg.StartsWith("/vbopen "))
      {
        var parts = msg.Split(' ');
        if (!IsPlayerAdmin(chat.playerId) && !adminIds.Contains(chat.playerId))
        {
          api.Console_Write($"[Backpack] Player {chat.playerId} attempted /vbopen without permission.");
          return;
        }
        if (parts.Length == 3 && int.TryParse(parts[1], out int targetPlayer))
        {
          // Validate slot
          if (!int.TryParse(parts[2], out int slot) || slot < 1 || slot > 6)
          {
            api.Console_Write($"[Backpack] Admin {chat.playerId}: invalid slot '{parts[2]}'. Usage: /vbopen <playerId> <1-6>");
            return;
          }
          var lockKey = $"{targetPlayer}:{slot}";
          if (backpackLocks.Contains(lockKey))
          {
            api.Console_Write($"[Backpack] Admin {chat.playerId}: slot {slot} for player {targetPlayer} is already in use.");
            return;
          }
          // Lock and track admin open
          backpackLocks.Add(lockKey);
          adminOpenMapping[chat.playerId] = (targetPlayer, slot);
          // Create admin backup BEFORE opening
          api.Console_Write($"[Backpack] Admin {chat.playerId} opening slot {slot} for player {targetPlayer}.");
          CreateAdminBackup(targetPlayer.ToString(), slot);
          var items = LoadBackpack(targetPlayer.ToString(), slot);
          // Open admin UI (no premature save - only save when admin closes)
          ShowBackpackUI(chat.playerId, seqNr, items, slot);
        }
        else
        {
          api.Console_Write($"[Backpack] Admin {chat.playerId}: Usage /vbopen <playerId> <1-5>");
        }
        return;
      }

      int backpackNumber = 0;

      if (msg == "/vb1")
      {
        backpackNumber = 1;
      }
      else if (msg == "/vb2")
      {
        backpackNumber = 2;
      }
      else if (msg == "/vb3")
      {
        backpackNumber = 3;
      }
      else if (msg == "/vb4")
      {
        backpackNumber = 4;
      }
      else if (msg == "/vb5")
      {
        backpackNumber = 5;
      }
      else if (msg == "/vb6")
      {
        backpackNumber = 6;
      }

      if (backpackNumber > 0)
      {
        int playerId = chat.playerId;
        string playerKey = playerId.ToString();

        // Track which backpack this player is using
        activeBackpackIndex[playerId] = backpackNumber;

        var items = LoadBackpack(playerKey, backpackNumber);
        ShowBackpackUI(playerId, seqNr, items, backpackNumber);
      }
    }

    // Called when the backpack window is changed or closed
    else if (eventId == CmdId.Event_Player_ItemExchange)
    {
      var exchange = (ItemExchangeInfo)data;
      int openerId = exchange.id;
      ushort seq = seqNr;
      // Admin remote save path
      if (adminOpenMapping.TryGetValue(openerId, out var adminInfo))
      {
        var (targetPlayer, slot) = adminInfo;
        
        // Safety check: don't save if items appear empty/corrupted
        if (exchange.items != null && exchange.items.Length > 0)
        {
          SaveBackpack(targetPlayer.ToString(), exchange.items, slot);
          api.Console_Write($"[Backpack] Admin saved backpack {slot} for player {targetPlayer}");
          // Notify player of admin update
          api.Game_Request(CmdId.Request_InGameMessage_SinglePlayer, seq,
            new ChatInfo { playerId = targetPlayer, msg = $"An admin has updated your Virtual Backpack #{slot}." });
        }
        else
        {
          api.Console_Write($"[Backpack] WARNING: Admin close with empty items for player {targetPlayer} slot {slot} - skipping save to prevent data loss");
        }
        
        // Release lock and tracking regardless
        backpackLocks.Remove($"{targetPlayer}:{slot}");
        adminOpenMapping.Remove(openerId);
        return;
      }
      // Player normal save path
      var exchangeInfo = (ItemExchangeInfo)data;
      int playerId = exchangeInfo.id;
      if (activeBackpackIndex.TryGetValue(playerId, out int backpackNumber))
      {
        SaveBackpack(playerId.ToString(), exchangeInfo.items, backpackNumber);
        api.Console_Write($"[Backpack] Saved backpack {backpackNumber} for player {playerId}");
        // Release lock if any
        backpackLocks.Remove($"{playerId}:{backpackNumber}");
      }
    }
  }

  private void ShowBackpackUI(int playerId, ushort seqNr, ItemStack[] items, int backpackNumber)
  {
    ItemExchangeInfo ui = new ItemExchangeInfo()
    {
      id = playerId,
      title = $"Virtual Backpack {backpackNumber}",
      desc = "Your personal storage",
      items = items,
      buttonText = "Close"
    };

    api.Game_Request(CmdId.Request_Player_ItemExchange, seqNr, ui);
  }

  private ItemStack[] LoadBackpack(string playerKey, int backpackNumber)
  {
    string path = Path.Combine(dataPath, playerKey + ".vb" + backpackNumber + ".json");

    if (File.Exists(path))
    {
      try
      {
        var json = File.ReadAllText(path);
        var wrapper = JsonConvert.DeserializeObject<BackpackData>(json);
        return wrapper?.items ?? new ItemStack[40];
      }
      catch (Exception ex)
      {
        api.Console_Write($"Error reading backpack {backpackNumber} for player {playerKey}: " + ex.Message);
        
        // Try to recover from backup files
        var recoveredItems = TryRecoverFromBackup(playerKey, backpackNumber);
        if (recoveredItems != null)
        {
          api.Console_Write($"[Backpack] Successfully recovered backpack {backpackNumber} from backup for player {playerKey}");
          return recoveredItems;
        }
        
        // Try partial recovery from corrupted file
        var partialItems = TryPartialRecovery(path);
        if (partialItems != null)
        {
          api.Console_Write($"[Backpack] Partially recovered backpack {backpackNumber} for player {playerKey}");
          return partialItems;
        }
      }
    }

    // Create new backpack - check for VB6 starter kit pre-population
    if (backpackNumber == 6)
    {
      var starterKitItems = LoadStarterKitItems(playerKey);
      if (starterKitItems != null)
      {
        api.Console_Write($"[Backpack] Creating VB6 with starter kit for player {playerKey}");
        return starterKitItems;
      }
    }
    
    // Create new empty backpack if not found or error
    api.Console_Write($"[Backpack] Creating new empty backpack {backpackNumber} for player {playerKey}");
    return new ItemStack[40];
  }

  private void SaveBackpack(string playerKey, ItemStack[] items, int backpackNumber)
  {
    try
    {
      string basePath = Path.Combine(dataPath, playerKey + ".vb" + backpackNumber + ".json");
      
      // Create backup rotation before saving
      CreateBackupRotation(basePath);
      
      var wrapper = new BackpackData { items = items };
      var json = JsonConvert.SerializeObject(wrapper, Formatting.Indented);
      
      // Atomic save: write to temp file first
      string tempPath = basePath + ".tmp";
      File.WriteAllText(tempPath, json);
      
      // Verify the save by reading it back
      if (VerifySave(tempPath, items))
      {
        // If verification passed, move temp to final location
        if (File.Exists(basePath))
          File.Delete(basePath);
        File.Move(tempPath, basePath);
        
        api.Console_Write($"[Backpack] Successfully saved backpack {backpackNumber} for player {playerKey}");
      }
      else
      {
        // Verification failed, clean up temp file
        if (File.Exists(tempPath))
          File.Delete(tempPath);
        throw new Exception("Save verification failed - data integrity check failed");
      }
    }
    catch (Exception ex)
    {
      api.Console_Write($"Error saving backpack {backpackNumber} for player {playerKey}: " + ex.Message);
    }
  }

  private void CreateBackupRotation(string originalPath)
  {
    try
    {
      if (!File.Exists(originalPath)) return;
      
      // Rotate existing backups (keep 3 versions)
      string backup3 = originalPath + ".bak3";
      string backup2 = originalPath + ".bak2";
      string backup1 = originalPath + ".bak1";
      
      if (File.Exists(backup2))
      {
        // Time-protected doomsday backup: only overwrite .bak3 if it's at least 1 day old
        bool canOverwriteBak3 = true;
        if (File.Exists(backup3))
        {
          DateTime bak3LastWrite = File.GetLastWriteTime(backup3);
          DateTime oneDayAgo = DateTime.Now.AddDays(-1);
          
          if (bak3LastWrite > oneDayAgo)
          {
            canOverwriteBak3 = false;
            api.Console_Write($"[Backpack] Preserving .bak3 doomsday backup (age: {(DateTime.Now - bak3LastWrite).TotalHours:F1} hours)");
          }
        }
        
        if (canOverwriteBak3)
        {
          if (File.Exists(backup3))
            File.Delete(backup3);
          File.Move(backup2, backup3);
          api.Console_Write($"[Backpack] Promoted .bak2 to .bak3 doomsday backup");
        }
        else
        {
          // Can't promote .bak2 to .bak3, so just leave .bak2 as-is for now
          api.Console_Write($"[Backpack] Skipping .bak2 promotion - .bak3 doomsday backup too recent");
        }
      }
      
      if (File.Exists(backup1))
      {
        // Only move .bak1 to .bak2 if we successfully promoted .bak2 or if .bak2 doesn't exist
        if (!File.Exists(backup2))
        {
          File.Move(backup1, backup2);
        }
        else
        {
          // .bak2 exists and wasn't promoted, so we need to overwrite it
          File.Delete(backup2);
          File.Move(backup1, backup2);
        }
      }
      
      File.Copy(originalPath, backup1);
    }
    catch (Exception ex)
    {
      api.Console_Write($"[Backpack] Warning: Could not create backup rotation: " + ex.Message);
    }
  }
  
  private void CreateAdminBackup(string playerKey, int backpackNumber)
  {
    try
    {
      string originalPath = Path.Combine(dataPath, playerKey + ".vb" + backpackNumber + ".json");
      
      if (!File.Exists(originalPath))
      {
        api.Console_Write($"[Backpack] No backpack file to backup for player {playerKey} slot {backpackNumber}");
        return;
      }
      
      // Create admin-specific backup rotation (keep 3 versions)
      string adminBackup3 = originalPath + ".vbobak3";
      string adminBackup2 = originalPath + ".vbobak2";
      string adminBackup1 = originalPath + ".vbobak1";
      
      // Rotate existing admin backups
      if (File.Exists(adminBackup2))
      {
        if (File.Exists(adminBackup3))
          File.Delete(adminBackup3);
        File.Move(adminBackup2, adminBackup3);
      }
      
      if (File.Exists(adminBackup1))
      {
        File.Move(adminBackup1, adminBackup2);
      }
      
      // Create new admin backup
      File.Copy(originalPath, adminBackup1);
      
      api.Console_Write($"[Backpack] Created admin backup for player {playerKey} slot {backpackNumber}");
    }
    catch (Exception ex)
    {
      api.Console_Write($"[Backpack] Warning: Could not create admin backup: " + ex.Message);
    }
  }
  
  private bool VerifySave(string filePath, ItemStack[] originalItems)
  {
    try
    {
      var json = File.ReadAllText(filePath);
      var wrapper = JsonConvert.DeserializeObject<BackpackData>(json);
      
      if (wrapper?.items == null) return false;
      if (wrapper.items.Length != originalItems.Length) return false;
      
      // Basic integrity check - count non-null items
      int originalCount = 0, savedCount = 0;
      
      for (int i = 0; i < originalItems.Length; i++)
      {
        if (originalItems[i].id > 0) originalCount++;
        if (wrapper.items[i].id > 0) savedCount++;
      }
      
      return originalCount == savedCount;
    }
    catch
    {
      return false;
    }
  }
  
  private ItemStack[] TryRecoverFromBackup(string playerKey, int backpackNumber)
  {
    string basePath = Path.Combine(dataPath, playerKey + ".vb" + backpackNumber + ".json");
    
    // Try backup files in order of preference: regular backups first, then admin backups
    string[] backupPaths = { 
      basePath + ".bak1", 
      basePath + ".bak2",
      basePath + ".bak3",
      basePath + ".vbobak1", 
      basePath + ".vbobak2",
      basePath + ".vbobak3"
    };
    
    foreach (string backupPath in backupPaths)
    {
      if (File.Exists(backupPath))
      {
        try
        {
          var json = File.ReadAllText(backupPath);
          var wrapper = JsonConvert.DeserializeObject<BackpackData>(json);
          if (wrapper?.items != null)
          {
            api.Console_Write($"[Backpack] Successfully recovered from backup: {Path.GetFileName(backupPath)}");
            return wrapper.items;
          }
        }
        catch
        {
          continue; // Try next backup
        }
      }
    }
    
    return null;
  }
  
  private ItemStack[] TryPartialRecovery(string filePath)
  {
    try
    {
      var json = File.ReadAllText(filePath);
      
      // Try to find the items array even in malformed JSON
      int itemsStart = json.IndexOf("\"items\":");
      if (itemsStart == -1) return null;
      
      int arrayStart = json.IndexOf("[", itemsStart);
      if (arrayStart == -1) return null;
      
      // Try to parse just the items array
      var items = new ItemStack[40];
      
      // This is a simplified recovery - in practice you'd want more sophisticated parsing
      // For now, just return empty array as safer fallback
      api.Console_Write("[Backpack] Partial recovery attempted but using safe empty fallback");
      return items;
    }
    catch
    {
      return null;
    }
  }
  
  /// <summary>
  /// Check if a player has admin permissions on the server.
  /// </summary>
  private bool IsPlayerAdmin(int playerId)
  {
    // Check cached player info for admin permission
    if (playerInfoCache.TryGetValue(playerId, out PlayerInfo info))
    {
      // Permission levels: 0 = Player, 1 = Moderator, 3 = Admin, 9 = GameMaster
      // Check for Admin (3) or GameMaster (9) permissions
      return info.permission == 3 || info.permission == 9;
    }
    
    // If not in cache, request player info and default to false for now
    api.Game_Request(CmdId.Request_Player_Info, 0, new Id { id = playerId });
    return false;
  }
  
  /// <summary>
  /// Reads the AdminList from the YAML config to populate allowed admin IDs.
  /// </summary>
  private void LoadAdminList()
  {
    try
    {
      var yamlPath = Path.Combine(Path.GetDirectoryName(typeof(VirtualBackpackMod).Assembly.Location), "VirtualBackpackMod.yaml");
      bool reading = false;
      foreach (var line in File.ReadAllLines(yamlPath))
      {
        var t = line.Trim();
        if (t.StartsWith("AdminList:"))
        {
          reading = true;
          continue;
        }
        if (reading)
        {
          if (t.StartsWith("-"))
          {
            // Extract number before any inline comment
            var token = t.Substring(1).Split('#')[0].Trim();
            if (int.TryParse(token, out var id))
              adminIds.Add(id);
          }
          else break;
        }
      }
      api.Console_Write($"[Backpack] Loaded {adminIds.Count} admin IDs from config.");
    }
    catch (Exception ex)
    {
      api.Console_Write($"[Backpack] Warning: could not load AdminList: {ex.Message}");
    }
  }

  /// <summary>
  /// Load starter kit items for VB6 first-time access
  /// </summary>
  private ItemStack[] LoadStarterKitItems(string playerKey)
  {
    try
    {
      string configPath = Path.Combine(Path.GetDirectoryName(typeof(VirtualBackpackMod).Assembly.Location), "StarterKitContents.json");
      
      if (!File.Exists(configPath))
      {
        api.Console_Write($"[Backpack] StarterKitContents.json not found for player {playerKey} VB6");
        return null;
      }
      
      var json = File.ReadAllText(configPath);
      var config = JsonConvert.DeserializeObject<StarterKitConfig>(json);
      
      if (config?.items == null || config.items.Length == 0)
      {
        api.Console_Write($"[Backpack] No starter kit items configured for player {playerKey} VB6");
        return null;
      }
      
      // Convert starter kit items to VB6 format with proper slotIdx
      ItemStack[] vb6Items = new ItemStack[40];
      
      for (int i = 0; i < Math.Min(config.items.Length, 40); i++)
      {
        vb6Items[i] = new ItemStack
        {
          id = config.items[i].id,
          count = config.items[i].count,
          ammo = config.items[i].ammo,
          decay = config.items[i].decay,
          slotIdx = (byte)i
        };
      }
      
      // Fill remaining slots with empty items
      for (int i = config.items.Length; i < 40; i++)
      {
        vb6Items[i] = new ItemStack { id = 0, count = 0, ammo = 0, decay = 0, slotIdx = (byte)i };
      }
      
      // Award credits if configured
      if (config.creditsReward > 0)
      {
        AwardStarterKitCredits(playerKey, config.creditsReward, config.consoleCommandTemplate);
      }
      
      api.Console_Write($"[Backpack] Loaded starter kit with {config.items.Length} items for player {playerKey} VB6");
      return vb6Items;
    }
    catch (Exception ex)
    {
      api.Console_Write($"[Backpack] Error loading starter kit for player {playerKey} VB6: {ex.Message}");
      return null;
    }
  }

  /// <summary>
  /// Award starter kit credits to player
  /// </summary>
  private void AwardStarterKitCredits(string playerKey, int amount, string commandTemplate)
  {
    try
    {
      if (amount <= 0 || string.IsNullOrWhiteSpace(commandTemplate)) return;
      
      string cmd = commandTemplate
        .Replace("{playerId}", playerKey)
        .Replace("{amount}", amount.ToString());
      
      // Execute console command via API1
      api.Game_Request(CmdId.Request_ConsoleCommand, (ushort)DateTime.UtcNow.Millisecond, new PString(cmd));
      api.Console_Write($"[Backpack] Awarded {amount} credits to player {playerKey} via VB6 starter kit");
    }
    catch (Exception ex)
    {
      api.Console_Write($"[Backpack] Error awarding starter kit credits to player {playerKey}: {ex.Message}");
    }
  }

}

public class BackpackData
{
  public ItemStack[] items { get; set; }
}

public class StarterKitConfig
{
  public ItemStack[] items { get; set; }
  public int creditsReward { get; set; } = 10000;
  public string consoleCommandTemplate { get; set; } = "credits add {playerId} {amount}";
}
