# VirtualBackpackMod

[![Platform](https://img.shields.io/badge/Platform-Empyrion-blue?style=for-the-badge&logo=steam)](https://store.steampowered.com/app/383120/Empyrion__Galactic_Survival/)
[![Framework](https://img.shields.io/badge/.NET-Standard%202.0-purple?style=for-the-badge&logo=dotnet)](https://dotnet.microsoft.com/)
[![Version](https://img.shields.io/badge/Version-2.0.1-orange?style=for-the-badge)](https://github.com/chaosz5050/empyrion-mods/tree/main/VirtualBackpackMod)
[![License](https://img.shields.io/badge/License-CC%20BY--NC--SA%204.0-red?style=for-the-badge)](https://creativecommons.org/licenses/by-nc-sa/4.0/)

## Overview

VirtualBackpackMod provides each player with six virtual backpacks (`/vb1`–`/vb6`) in Empyrion. Backpacks are persisted per-player as JSON with atomic saves, backup rotation, and simple recovery. VB6 includes special starter kit functionality for new players.

### Features
- Open personal backpack via chat commands:
  - `/vb1` … `/vb6`
- **VB6 Starter Kit**: First-time access to VB6 pre-loads with configurable starter items and credits
- Automatic save on UI close, with backup files (`.bak1`, `.bak2`).
- Atomic writes with verification to prevent data corruption.

### Remote Admin Enhancement
- Configure allowed admin player IDs in `VirtualBackpackMod.yaml` under `AdminList:`
  (entries support inline comments, but must be in form `- 123456789` before any `#`)
- Locks to prevent concurrent open of the same player-slot by multiple users

## VB6 Starter Kit

VB6 serves dual purposes:
1. **First Access**: Pre-loaded with starter kit items and credit rewards for new players
2. **Ongoing Use**: Functions as a normal persistent backpack after first use

**Configuration**: Edit `StarterKitContents.json` to customize starter items and credit rewards.

**💡 Pro Tip - Easy Configuration Workflow:**
1. Use `/vb6` as admin to open your own VB6 
2. Populate it with desired starter kit items using in-game spawning/cheats
3. Copy the generated `adminPlayerId.vb6.json` content to `StarterKitContents.json`
4. Set `creditsReward: 0` if including credits as an item (recommended)

This ensures proper item IDs, ammo counts, and decay values without guesswork!

Example `StarterKitContents.json`:
```json
{
  "items": [
    {
      "id": 4119,
      "count": 1,
      "ammo": 125,
      "decay": 0
    },
    {
      "id": 7906,
      "count": 10000,
      "ammo": 0,
      "decay": 0
    }
  ],
  "creditsReward": 0,
  "consoleCommandTemplate": ""
}
```

## Remote Admin Access

Administrators can remotely open a player's backpack without needing to travel in-game.

**Command:**
```
/vbopen <playerId> <slot>
```

**Requirements:**
- Caller must be an admin.
- `<slot>` is between 1 and 6.

**Behavior:**
1. The player’s data is force-saved to disk (flushes in-UI edits) and the action is logged to the server console.
2. Admin sees the same backpack UI loaded with the player’s items (no in-chat popup currently).
3. On UI close, admin’s changes are persisted back to the player’s backpack file.

## Safety & Data Integrity

- **Time-Protected Backups**: `.bak3` files serve as "doomsday backups" that are only overwritten if they're at least 24 hours old
- **Triple Backup Rotation**: Player backups are rotated (`.bak1`, `.bak2`, `.bak3`) with time protection on the oldest backup
- **Atomic Saves**: All saves are written to a `.tmp` file and verified before replacing the primary JSON
- **Data Loss Prevention**: Enhanced backup system prevents rapid rotation from wiping out all good backups
- **Recovery Fallback**: If a save or recovery error occurs, the system attempts recovery from backup files in order of preference

## Installation

Copy `VirtualBackpackMod.dll`, `VirtualBackpackMod.yaml` (updated with your `AdminList:`), `StarterKitContents.json`, and dependencies into your Empyrion `Mods` folder.

---
*For full implementation details, see `VirtualBackpackMod.cs`.*
