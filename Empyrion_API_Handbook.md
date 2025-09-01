# Empyrion Galactic Survival - API Modding Handbook

*A comprehensive guide to Empyrion modding based on real-world mod development experience.*

## Table of Contents

1. [Project Setup & Architecture](#project-setup--architecture)
2. [Core API Systems](#core-api-systems)
3. [Essential Operations](#essential-operations)
4. [Data Management](#data-management)
5. [Common Pitfalls & Solutions](#common-pitfalls--solutions)
6. [Item & Configuration Systems](#item--configuration-systems)
7. [Performance & Threading](#performance--threading)
8. [Troubleshooting Guide](#troubleshooting-guide)
9. [Code Examples](#code-examples)

---

## Project Setup & Architecture

### Basic Project Structure

```
YourMod/
├── YourMod.cs              # Main implementation
├── YourMod.csproj          # Project file
├── YourMod.yaml            # Empyrion mod metadata
├── References/             # Game DLLs
│   ├── Mif.dll
│   ├── ModApi.dll
│   └── protobuf-net.dll
├── config.json            # Mod configuration (generated)
└── README.md
```

### Essential .csproj Template

```xml
<Project Sdk="Microsoft.NET.Sdk">
  <PropertyGroup>
    <TargetFramework>netstandard2.0</TargetFramework>
    <GenerateAssemblyInfo>false</GenerateAssemblyInfo>
  </PropertyGroup>

  <ItemGroup>
    <Reference Include="Mif">
      <HintPath>References/Mif.dll</HintPath>
      <Private>true</Private>
    </Reference>
    <Reference Include="ModApi">
      <HintPath>References/ModApi.dll</HintPath>
      <Private>true</Private>
    </Reference>
    <Reference Include="protobuf-net">
      <HintPath>References/protobuf-net.dll</HintPath>
      <Private>true</Private>
    </Reference>
  </ItemGroup>

  <ItemGroup>
    <PackageReference Include="Newtonsoft.Json" Version="13.0.3">
      <PrivateAssets>all</PrivateAssets>
    </PackageReference>
  </ItemGroup>

  <Target Name="ForceCopyNewtonsoftJson" AfterTargets="Build">
    <ItemGroup>
      <EmbeddedResource Include="$(OutputPath)Newtonsoft.Json.dll" />
    </ItemGroup>
  </Target>
</Project>
```

### YAML Metadata Template

```yaml
ModName: YourModName
Version: 1.0.0
Author: YourName
Description: Your mod description
GameVersion: 1.0.0
ModGuid: YourModName-guid-here-unique-identifier
ModType: ModInterface
ModDllName: YourModName.dll
ModClass: YourModName
LoadOnStart: true
LoadOn:
  - Dedicated
  - Playfield
Dependencies: []
```

---

## Core API Systems

### Dual API Architecture

Empyrion uses two separate API systems that serve different purposes:

```csharp
public class YourMod : IMod, ModInterface
{
    private IModApi api2;     // API2: messaging, application services
    private ModGameAPI api1;  // API1: events, game requests, console commands

    // API2 Implementation
    public void Init(IModApi api)
    {
        api2 = api;
        // Initialize paths, load config, setup
    }

    public void Shutdown() { }

    // API1 Implementation  
    public void Game_Start(ModGameAPI dediAPI)
    {
        api1 = dediAPI;
        // Game API is now available
    }

    public void Game_Exit() { }
    public void Game_Update() { }

    public void Game_Event(CmdId eventId, ushort seqNr, object data)
    {
        // Handle game events
    }
}
```

### API Usage Guidelines

- **API2 (`IModApi api2`)**: Use for messaging, file operations, application-level services
- **API1 (`ModGameAPI api1`)**: Use for game requests, player operations, game events

---

## Essential Operations

### Item Delivery - The Right Way

**❌ WRONG - Console Commands (Don't Work for Other Players):**
```csharp
// This fails - console commands can't target other players
api1.Game_Request(CmdId.Request_ConsoleCommand, seqNr, 
    new PString($"give {playerId} {itemId} {amount}"));
```

**✅ RIGHT - Direct API Calls:**
```csharp
public bool GiveItemToPlayer(int playerId, int itemId, int amount)
{
    try
    {
        var itemData = new IdItemStack
        {
            id = playerId,
            itemStack = new ItemStack
            {
                id = itemId,
                count = amount,
                slotIdx = 0,
                ammo = 0,
                decay = 0
            }
        };
        
        api1.Game_Request(CmdId.Request_Player_AddItem, 
            (ushort)DateTime.UtcNow.Millisecond, itemData);
        return true;
    }
    catch (Exception ex)
    {
        Log($"Failed to give item: {ex.Message}");
        return false;
    }
}
```

### Player Health Manipulation

```csharp
public void SetPlayerHealth(int playerId, int health)
{
    var playerInfoSet = new PlayerInfoSet
    {
        entityId = playerId,
        health = health
    };
    
    api1.Game_Request(CmdId.Request_Player_SetPlayerInfo, 
        (ushort)DateTime.UtcNow.Millisecond, playerInfoSet);
}

// Example: Near-death experience (drop to 1 HP)
SetPlayerHealth(playerId, 1);
```

### Player Name Resolution with Caching

Player names aren't immediately available and require async lookup:

```csharp
private ConcurrentDictionary<int, string> playerNameCache = new ConcurrentDictionary<int, string>();
private ConcurrentDictionary<int, DateTime> pendingPlayerLookups = new ConcurrentDictionary<int, DateTime>();

public string ResolveDisplayName(int playerId)
{
    // Check cache first
    if (playerNameCache.TryGetValue(playerId, out string cachedName))
        return cachedName;
    
    // Avoid duplicate lookups
    if (pendingPlayerLookups.ContainsKey(playerId))
        return $"Player_{playerId}";
    
    // Request player info asynchronously
    Task.Run(async () => await RequestPlayerInfoAsync(playerId));
    
    return $"Player_{playerId}";
}

private async Task RequestPlayerInfoAsync(int playerId)
{
    try
    {
        pendingPlayerLookups[playerId] = DateTime.UtcNow;
        
        // Request player info
        api1.Game_Request(CmdId.Request_Player_Info, 
            (ushort)DateTime.Now.Millisecond, new Id(playerId));
        
        await Task.Delay(500); // Wait for response
        
        // Response handled in Game_Event
    }
    finally
    {
        pendingPlayerLookups.TryRemove(playerId, out _);
    }
}

// Handle response in Game_Event
public void Game_Event(CmdId eventId, ushort seqNr, object data)
{
    if (eventId == CmdId.Event_Player_Info && data is PlayerInfo playerInfo)
    {
        if (playerInfo.entityId > 0 && !string.IsNullOrEmpty(playerInfo.playerName))
        {
            playerNameCache[playerInfo.entityId] = playerInfo.playerName;
        }
    }
}
```

### Messaging Systems

**Private Messages:**
```csharp
private void SendPrivate(int playerId, string text)
{
    var msg = new MessageData
    {
        Channel = Eleon.MsgChannel.SinglePlayer,
        RecipientEntityId = playerId,
        Text = text,
        SenderNameOverride = "YourModName"
    };
    api2.Application.SendChatMessage(msg);
}
```

**Broadcast Messages:**
```csharp
private void Broadcast(string text)
{
    var msg = new MessageData
    {
        Channel = Eleon.MsgChannel.Global,
        Text = text,
        SenderNameOverride = "YourModName"
    };
    api2.Application.SendChatMessage(msg);
}
```

---

## Data Management

### Thread-Safe Configuration Loading

```csharp
private readonly object ioLock = new object();

private void LoadConfig()
{
    lock (ioLock)
    {
        try
        {
            if (!File.Exists(configPath))
            {
                config = DefaultConfig();
                SaveConfig();
                return;
            }

            var json = File.ReadAllText(configPath);
            config = JsonConvert.DeserializeObject<YourConfig>(json) ?? DefaultConfig();
        }
        catch (Exception ex)
        {
            Log($"Config load failed: {ex.Message} - using defaults");
            config = DefaultConfig();
        }
    }
}

private void SaveConfig()
{
    lock (ioLock)
    {
        File.WriteAllText(configPath, JsonConvert.SerializeObject(config, Formatting.Indented));
    }
}
```

### Player Data Persistence

```csharp
private PlayerProfile LoadOrCreateProfile(int playerId)
{
    lock (ioLock)
    {
        var path = GetProfilePath(playerId);
        if (!File.Exists(path))
        {
            var profile = new PlayerProfile();
            SaveProfile(playerId, profile);
            return profile;
        }

        try
        {
            var json = File.ReadAllText(path);
            return JsonConvert.DeserializeObject<PlayerProfile>(json) ?? new PlayerProfile();
        }
        catch
        {
            // Corrupted file - reset
            var profile = new PlayerProfile();
            SaveProfile(playerId, profile);
            return profile;
        }
    }
}

private void SaveProfile(int playerId, PlayerProfile profile)
{
    lock (ioLock)
    {
        var path = GetProfilePath(playerId);
        File.WriteAllText(path, JsonConvert.SerializeObject(profile, Formatting.Indented));
    }
}

private string GetProfilePath(int playerId) => Path.Combine(dataDir, $"{playerId}.json");
```

---

## Common Pitfalls & Solutions

### 1. Console Commands vs API Calls

**Problem**: Console commands like `give`, `credits add` don't work for targeting other players.

**Solution**: Use direct API calls like `Request_Player_AddItem`, `Request_Player_SetPlayerInfo`.

### 2. Item IDs and Scenario Compatibility

**Problem**: Generic Empyrion item IDs don't match scenario-specific items.

**Solutions**:
- Test items in-game first (save to containers, check generated JSON)
- Parse scenario configuration files (ItemsConfig.ecf, BlocksConfig.ecf)
- Create item name mapping systems for different scenarios

### 3. Player Name Resolution

**Problem**: Player names aren't immediately available, causing "Player_123" display.

**Solution**: Implement async lookup with caching system (see code example above).

### 4. Thread Safety

**Problem**: File operations and shared data cause crashes or corruption.

**Solution**: Use locks around all file I/O and shared collections.

### 5. Sequence Numbers

**Problem**: API requests fail with invalid sequence numbers.

**Solution**: Use dynamic sequence numbers:
```csharp
api1.Game_Request(CmdId.Request_Player_Info, 
    (ushort)DateTime.UtcNow.Millisecond, data);
```

---

## Item & Configuration Systems

### Finding Correct Item IDs

**Method 1: In-Game Testing**
1. Put items in containers/inventories
2. Check generated JSON files for exact item IDs
3. Verify items work with your target scenario

**Method 2: Scenario Configuration Analysis**
```bash
# Search for item ID in scenario configs
grep -r "ItemId: 4119" /path/to/scenario/Content/Configuration/
grep -r "Block Id: 711" /path/to/scenario/Content/Configuration/
```

**Method 3: VirtualBackpack Pattern**
Use proven working item IDs from existing mods like VirtualBackpack:

```json
{
  "items": [
    { "id": 4119, "count": 1, "ammo": 125, "decay": 0 },
    { "id": 7906, "count": 10000, "ammo": 0, "decay": 0 }
  ]
}
```

### Credits and Currency Systems

**Star Salvage Credits System:**
- Standard Credits: `Player.Credit` (direct credit manipulation)
- Money Cards:
  - `MoneyCard` (ID 248): Up to 50,000 credits
  - `MoneyCard2` (ID 3809): Up to 250,000 credits  
  - `MoneyCard3` (ID 3810): Up to 500,000 credits
  - `MoneyCard4` (ID 3811): Up to 1,000,000 credits

**Usage Example:**
```csharp
// Give credits directly (if supported)
GiveItemToPlayer(playerId, 4344, 1000); // 1000 credits (Star Salvage)

// Give money card
GiveItemToPlayer(playerId, 248, 50000);   // Money card with 50k credits
```

---

## Performance & Threading

### Thread-Safe Collections

```csharp
// Use concurrent collections for shared data
private ConcurrentDictionary<int, string> playerCache = new ConcurrentDictionary<int, string>();
private ConcurrentDictionary<int, DateTime> activeOperations = new ConcurrentDictionary<int, DateTime>();
```

### Async Operations

```csharp
// Use Task.Run for non-blocking operations
Task.Run(async () => {
    await SomeLongRunningOperation();
});

// Handle responses in Game_Event
public void Game_Event(CmdId eventId, ushort seqNr, object data)
{
    if (eventId == CmdId.Event_Player_Info)
    {
        ProcessPlayerInfoResponse(data as PlayerInfo);
    }
}
```

### Efficient Logging

```csharp
private void SafeLog(string message)
{
    try 
    { 
        File.AppendAllText(logPath, $"{DateTime.UtcNow:yyyy-MM-dd HH:mm:ss} {message}\n"); 
    }
    catch { /* Swallow logging errors */ }
}
```

---

## Troubleshooting Guide

### Common Build Errors

**Missing References**
```
The type or namespace name 'ModGameAPI' could not be found
```
**Solution**: Ensure Mif.dll, ModApi.dll are in References/ folder with correct HintPath.

**Newtonsoft.Json Issues**
```
Could not load file or assembly 'Newtonsoft.Json'
```
**Solution**: Use the ForceCopyNewtonsoftJson target in .csproj.

### Runtime Issues

**Mod Not Loading**
- Check YAML filename matches DLL name exactly
- Verify ModClass name matches your class name
- Check game logs for load errors

**API Calls Failing**
- Ensure api1 is not null before use
- Check sequence numbers are valid
- Verify data structures match API expectations

**Items Not Delivered**
- Test item IDs in-game first
- Use `Request_Player_AddItem`, not console commands
- Check player is online and inventory isn't full

---

## Code Examples

### Complete Basic Mod Template

```csharp
using System;
using System.IO;
using System.Collections.Generic;
using System.Collections.Concurrent;
using Newtonsoft.Json;
using Eleon.Modding;
using Eleon;

public class YourMod : IMod, ModInterface
{
    private IModApi api2;
    private ModGameAPI api1;
    
    private string modDir;
    private string configPath;
    private string logPath;
    private YourConfig config;
    private readonly object ioLock = new object();

    // IMod Implementation
    public void Init(IModApi api)
    {
        api2 = api;
        modDir = Path.GetDirectoryName(System.Reflection.Assembly.GetExecutingAssembly().Location);
        configPath = Path.Combine(modDir, "config.json");
        logPath = Path.Combine(modDir, "mod.log");
        
        LoadConfig();
        SafeLog("Mod initialized");
    }

    public void Shutdown()
    {
        SafeLog("Mod shutdown");
    }

    // ModInterface Implementation
    public void Game_Start(ModGameAPI dediAPI)
    {
        api1 = dediAPI;
        SafeLog("Game API connected");
    }

    public void Game_Exit() { }
    public void Game_Update() { }

    public void Game_Event(CmdId eventId, ushort seqNr, object data)
    {
        try
        {
            if (eventId == CmdId.Event_ChatMessage && data is ChatInfo chat)
            {
                HandleChatMessage(chat);
            }
        }
        catch (Exception ex)
        {
            SafeLog($"Event error: {ex}");
        }
    }

    private void HandleChatMessage(ChatInfo chat)
    {
        var message = chat.msg?.Trim();
        if (string.IsNullOrEmpty(message)) return;

        if (message.StartsWith("/yourcommand"))
        {
            // Handle your command
            SendPrivate(chat.playerId, "Command executed!");
        }
    }

    private void SendPrivate(int playerId, string text)
    {
        var msg = new MessageData
        {
            Channel = Eleon.MsgChannel.SinglePlayer,
            RecipientEntityId = playerId,
            Text = text,
            SenderNameOverride = "YourMod"
        };
        api2.Application.SendChatMessage(msg);
    }

    private void LoadConfig()
    {
        lock (ioLock)
        {
            try
            {
                if (!File.Exists(configPath))
                {
                    config = new YourConfig();
                    SaveConfig();
                    return;
                }

                var json = File.ReadAllText(configPath);
                config = JsonConvert.DeserializeObject<YourConfig>(json) ?? new YourConfig();
            }
            catch (Exception ex)
            {
                SafeLog($"Config load failed: {ex.Message}");
                config = new YourConfig();
            }
        }
    }

    private void SaveConfig()
    {
        lock (ioLock)
        {
            File.WriteAllText(configPath, JsonConvert.SerializeObject(config, Formatting.Indented));
        }
    }

    private void SafeLog(string message)
    {
        try
        {
            File.AppendAllText(logPath, $"{DateTime.UtcNow:yyyy-MM-dd HH:mm:ss} {message}\n");
        }
        catch { }
    }

    private class YourConfig
    {
        public string SomeOption { get; set; } = "default";
        public int SomeValue { get; set; } = 100;
    }
}
```

---

## Contributing

This handbook is based on real-world Empyrion modding experience. If you discover additional patterns, fixes, or improvements, contributions are welcome!

### Tested Patterns

All code examples in this handbook have been tested and used in production mods including:
- WheelOfFortuneMod (gambling system with item delivery)
- PlayerStatusMod (player management and messaging)
- VirtualBackpackMod (inventory management)

### Version Compatibility

- **Empyrion Version**: 1.0+
- **.NET Standard**: 2.0
- **API Version**: Current as of 2024

---

*This handbook represents hundreds of hours of trial-and-error development. Use it to build amazing mods without the pain!*