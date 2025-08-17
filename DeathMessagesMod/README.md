# DeathMessagesMod

[![Platform](https://img.shields.io/badge/Platform-Empyrion-blue?style=for-the-badge&logo=steam)](https://store.steampowered.com/app/383120/Empyrion__Galactic_Survival/)
[![Framework](https://img.shields.io/badge/.NET-Standard%202.0-purple?style=for-the-badge&logo=dotnet)](https://dotnet.microsoft.com/)
[![Version](https://img.shields.io/badge/Version-2.0.0-orange?style=for-the-badge)](https://github.com/chaosz5050/DeathMessagesMod)
[![License](https://img.shields.io/badge/License-CC%20BY--NC--SA%204.0-red?style=for-the-badge)](https://creativecommons.org/licenses/by-nc-sa/4.0/)

## Overview

DeathMessagesMod brings humor and context-awareness to player deaths in Empyrion Galactic Survival. Instead of generic death notifications, this mod displays entertaining messages that actually reflect how the player died - whether they fell, got shot, suffocated, or met their end in other creative ways.

## Features

### 🎯 **Context-Aware Death Messages**
- **Real Death Cause Detection**: Uses Empyrion's Statistics API to capture actual death causes
- **9 Death Categories**: Specific messages for each type of death (projectile, explosion, fall, etc.)
- **47+ Unique Messages**: Variety of humorous messages tailored to each death cause
- **Smart Fallbacks**: Graceful handling of unknown death causes

### 🛡️ **Clean Messaging System**
- **Hybrid API1+API2**: Eliminates duplicate messages that plague API1-only mods
- **Bracketed Format**: Death messages appear as `[Player forgot to breathe]` for clarity
- **Server Attribution**: Messages clearly identified as server announcements

### 🔧 **Technical Excellence**
- **No Duplicate Messages**: Uses proven API2 messaging pattern
- **Enhanced Logging**: Detailed console logs with death cause information
- **Error Resilience**: Comprehensive error handling and fallback systems

## Death Cause Categories

| Death Cause | Example Messages |
|-------------|------------------|
| **Unknown** | "died mysteriously", "experienced an unknown fate" |
| **Projectile** | "got shot down", "became target practice", "became Swiss cheese" |
| **Explosion** | "went out with a bang", "rapid unscheduled disassembly" |
| **Starvation** | "forgot food is important", "can't eat metal" |
| **Oxygen** | "forgot to breathe", "space is not user-friendly" |
| **Disease** | "should have washed hands", "alien germs" |
| **Drowning** | "forgot they weren't a fish", "gills optional" |
| **Fall Damage** | "gravity won", "forgot parachute", "maximum velocity" |
| **Suicide** | "rage-quit life", "pressed wrong button" |

## Example Output

```
[ChaoszMind forgot to pack a parachute]
[Steve experienced rapid unscheduled disassembly] 
[Alice discovered why spacesuits exist]
[Bob became Swiss cheese]
```

## How It Works

1. **Death Detection**: Monitors `Event_Statistics` for `PlayerDied` events
2. **Cause Analysis**: Extracts death cause code from `statsParam.int2` (0-8 range)
3. **Message Selection**: Chooses appropriate message from cause-specific arrays
4. **Clean Delivery**: Uses API2 messaging to prevent duplicates
5. **Enhanced Logging**: Records death cause names and codes for debugging

## Installation

Copy the following files to your Empyrion `Mods` folder:
- `DeathMessagesMod.dll`
- `DeathMessagesMod.yaml`
- `References/` folder with required DLLs

## Technical Details

- **Framework**: .NET Standard 2.0
- **API Approach**: Hybrid ModInterface + IMod for optimal compatibility
- **Death Cause Mapping**: Based on official Empyrion Statistics API enumeration
- **Message Encoding**: Uses seqNr encoding to pass death cause between events
- **Fallback Support**: API1 console commands as backup if API2 fails

## Configuration

The mod works out-of-the-box with no configuration required. Death messages are hard-coded for consistency and reliability across server restarts.

## Changelog

See [CHANGELOG.md](CHANGELOG.md) for detailed version history.

---

*Developed with ❤️ for the Empyrion Galactic Survival modding community*