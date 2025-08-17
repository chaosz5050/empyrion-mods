# Empyrion Mods Collection

[![Platform](https://img.shields.io/badge/Platform-Windows%20%7C%20Linux-blue?style=for-the-badge&logo=empyrion)](https://empyriongame.com/)
[![C#](https://img.shields.io/badge/C%23-.NET%20Standard%202.0-green?style=for-the-badge&logo=csharp)](https://docs.microsoft.com/en-us/dotnet/standard/net-standard)
[![Mods](https://img.shields.io/badge/Mods-5%20Available-orange?style=for-the-badge)](https://github.com/chaosz5050/empyrion-mods)
[![License](https://img.shields.io/badge/License-CC%20BY--NC--SA%204.0-red?style=for-the-badge)](https://creativecommons.org/licenses/by-nc-sa/4.0/)

A collection of Empyrion Galactic Survival mods created by René.

## 🎮 Available Mods

### DeathMessagesMod
**Context-aware death messages with humor**
- 47+ unique death messages based on actual death causes
- Real-time death cause detection via Statistics API
- Hybrid API1+API2 messaging for clean delivery

### PlayerStatusMod  
**Player join/leave announcements and server messaging**
- Welcome/goodbye messages for players
- Scheduled server announcements
- Player status tracking and caching

### VirtualBackpackMod
**Virtual inventory storage system**  
- Multiple virtual backpack slots per player
- Persistent storage across server restarts
- Easy access commands and management

### TriviaChallengeMod
**Interactive trivia system for players**
- Configurable trivia questions and answers
- Reward system for correct answers
- Customizable question pools

### EmergencyHomeTeleportMod
**Emergency teleportation for players**
- Quick home teleport functionality
- Emergency escape mechanics
- Configurable cooldowns and restrictions

## 🔧 Building

Each mod can be built independently:

```bash
cd ModName/
dotnet build -c Release
```

Build all mods:
```bash
for mod in */; do
    if [ -f "$mod"*.csproj ]; then
        echo "Building $mod"
        cd "$mod" && dotnet build -c Release && cd ..
    fi
done
```

## 📦 Installation

1. Build the mod using `dotnet build -c Release`
2. Copy the `.dll` and `.yaml` files to your Empyrion `Mods/` directory
3. Restart your Empyrion server

## 🤝 Contributing

Each mod maintains its own changelog and documentation. See individual mod directories for specific development information.

## 📄 License

Each mod may have its own license. Check individual mod directories for licensing information.

---

🤖 *Generated with [Claude Code](https://claude.ai/code)*