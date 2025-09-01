# WheelOfFortuneMod for Empyrion Galactic Survival

[![Platform](https://img.shields.io/badge/Platform-Empyrion-blue?style=for-the-badge&logo=steam)](https://store.steampowered.com/app/383120/Empyrion__Galactic_Survival/)
[![Framework](https://img.shields.io/badge/.NET-Standard%202.0-purple?style=for-the-badge&logo=dotnet)](https://dotnet.microsoft.com/)
[![Version](https://img.shields.io/badge/Version-1.0.0-orange?style=for-the-badge)](https://github.com/chaosz5050/empyrion-mods/tree/main/WheelOfFortuneMod)
[![License](https://img.shields.io/badge/License-CC%20BY--NC--SA%204.0-red?style=for-the-badge)](https://creativecommons.org/licenses/by-nc-sa/4.0/)

## Overview

WheelOfFortuneMod brings casino-style gambling excitement to Empyrion Galactic Survival! Players can spin a wheel of fortune for random prizes including salvage tokens, credits, and special experiences. The mod features daily spin limits, cooldowns, and configurable prize pools to maintain server balance.

## Features

- 🎰 **Wheel of Fortune Gambling**: Players spin for random rewards
- 💰 **Multiple Prize Types**: Salvage tokens, credits, and special experiences
- ⏰ **Smart Cooldown System**: Configurable cooldowns between spins (default: 5 minutes)
- 📅 **Daily Limits**: Prevents gambling addiction with configurable daily spin limits
- 🎯 **Balanced Prize Pool**: Configurable rewards with different rarity levels
- 💀 **Near Death Experience**: Special "prize" that drops players to 1 HP for dramatic effect
- 🔄 **Real-time Status**: Players can check remaining spins and cooldown time
- 📊 **Player Data Persistence**: Individual player profiles with automatic daily resets

## Commands

- `/wheel` - Spin the wheel of fortune
- `/wheel status` - Check remaining spins and cooldown time

## Prize System

The wheel features multiple prize categories:

### Salvage Tokens
- **1x Salvage Token** - Common reward for trading with I.S.I.
- **10x Salvage Tokens** - Good luck! 
- **100x Salvage Tokens** - Jackpot!

### Credits
- **1,000 Credits** - Small financial boost
- **10,000 Credits** - Decent payout
- **100,000 Credits** - Major jackpot!

### Special Experiences
- **Near Death Experience** - Drops player to 1 HP for thrill-seekers

## Configuration

The mod automatically generates `wheel_config.json` with the following structure:

```json
{
  "perPlayer": {
    "maxSpinsPerDay": 3,
    "cooldownSeconds": 300
  },
  "rewardPool": [
    {
      "type": "item",
      "amount": 1,
      "itemId": 7901,
      "label": "Salvage Token"
    }
  ],
  "messages": {
    "spinStart": "[WHEEL] Spinning the wheel for {player}…",
    "won": "[WHEEL] {player} won {reward}!",
    "cooldown": "[WHEEL] Cooldown active. Try again in {secs}s.",
    "limit": "[WHEEL] You already used all {count} spins for today.",
    "error": "[WHEEL] Something went wrong. Contact admin.",
    "adminError": "[WHEEL] Hey admin, your wheel is broken! Need at least 3 prizes. What kind of wheel has 0-2 options? That's not a wheel, that's a coin flip!"
  }
}
```

### Configuration Options

- **maxSpinsPerDay**: Maximum number of spins per player per day (default: 3)
- **cooldownSeconds**: Seconds between spins (default: 300 = 5 minutes)
- **rewardPool**: Array of available prizes with type, amount, itemId, and display label
- **messages**: Customizable chat messages for different wheel events

## Item Compatibility

WheelOfFortuneMod uses verified item IDs that work with the Star Salvage scenario:
- **Salvage Tokens (ID: 7901)**: Universal trading currency with I.S.I.
- **Credits (ID: 4344)**: Standard in-game currency

All item IDs have been tested and verified to work correctly with item delivery systems.

## Installation

1. Download the mod files to your Empyrion server's `Mods` directory
2. Ensure the mod structure matches:
   ```
   Mods/WheelOfFortuneMod/
   ├── WheelOfFortuneMod.dll
   ├── WheelOfFortuneMod.yaml
   └── wheel_config.json (auto-generated)
   ```
3. Restart your Empyrion server
4. The mod will automatically generate default configuration on first run

## Requirements

- **Empyrion Galactic Survival** (Dedicated Server)
- **.NET Standard 2.0** runtime
- **Server restart** required for installation

## Technical Details

- **Thread-safe operations**: Safe file I/O and player data management
- **Player name resolution**: Handles player lookup with caching for better performance
- **Daily reset system**: Automatically resets player spin counts at midnight UTC
- **Error handling**: Graceful failure handling with informative admin messages
- **Direct API integration**: Uses `Request_Player_AddItem` for reliable item delivery

## Data Storage

Player data is stored in individual JSON files:
- Location: `Mods/WheelOfFortuneMod/playerdata/`
- Format: `{playerId}.json`
- Contains: daily spin count, last spin time, current day tracking

## Contributing

This mod is part of the [Empyrion Mods Collection](https://github.com/chaosz5050/empyrion-mods). 

For development guidance, see the [Empyrion API Handbook](https://github.com/chaosz5050/empyrion-mods/blob/main/Empyrion_API_Handbook.md) which documents the patterns and APIs used in this mod.

## License

This work is licensed under a [Creative Commons Attribution-NonCommercial-ShareAlike 4.0 International License](https://creativecommons.org/licenses/by-nc-sa/4.0/).

---

*Spin responsibly! 🎰*