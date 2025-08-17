# TriviaChallengeMod

[![Platform](https://img.shields.io/badge/Platform-Empyrion-blue?style=for-the-badge&logo=steam)](https://store.steampowered.com/app/383120/Empyrion__Galactic_Survival/)
[![Framework](https://img.shields.io/badge/.NET-Standard%202.0-purple?style=for-the-badge&logo=dotnet)](https://dotnet.microsoft.com/)
[![Version](https://img.shields.io/badge/Version-1.0.0-orange?style=for-the-badge)](https://github.com/chaosz5050/empyrion-mods/tree/main/TriviaChallengeMod)
[![License](https://img.shields.io/badge/License-CC%20BY--NC--SA%204.0-red?style=for-the-badge)](https://creativecommons.org/licenses/by-nc-sa/4.0/)

## Overview

A sophisticated, timed multiplayer trivia challenge mod for Empyrion Galactic Survival. Players compete in scheduled multiple-choice rounds themed around gaming knowledge to win in-game credits. Features intelligent scheduling, randomization, and comprehensive admin controls.

## Features

### 🎮 **Game Mechanics**
- **Scheduled Rounds**: Automated hourly trivia sessions (fully configurable)
- **Multiple Choice Format**: A, B, C, D answer system with randomized order
- **Smart Question Pool**: Prevents repeat questions with configurable window
- **Real-time Scoring**: Automatic point calculation and winner determination
- **Credit Rewards**: In-game currency prizes for winners

### ⚙️ **Administrative Controls**
- **Flexible Scheduling**: Configurable timing, duration, and question count
- **Dry Run Mode**: Test rounds without payouts for admin testing
- **Live Management**: Start, stop, and reload commands during operation
- **Comprehensive Logging**: Detailed event tracking in `trivia.log`

### 🔧 **Technical Excellence**
- **JSON Configuration**: Easy customization through config files
- **Question Bank System**: Expandable question database with categories
- **State Persistence**: Maintains game state across server restarts
- **Error Resilience**: Robust error handling and recovery systems

## Installation

### Prerequisites
- Empyrion Galactic Survival Dedicated Server
- .NET Standard 2.0 Runtime

### Setup Steps
1. **Download**: Get the latest release from GitHub or compile from source
2. **Deploy**: Copy all files to your server's `Mods/TriviaChallengeMod` directory:
   - `TriviaChallengeMod.dll`
   - `TriviaChallengeMod.yaml`  
   - `References/` folder with required DLLs
3. **Auto-Configuration**: On first run, the mod automatically creates:
   - `trivia_config.json` — Server configuration settings
   - `questions.json` — Question bank (pre-loaded with gaming trivia)
   - `trivia_state.json` — Persistent game state
   - `trivia.log` — Comprehensive event logging

## Configuration (`trivia_config.json`)
- `schedule.mode` — e.g., `"hourly"`
- `schedule.minute` — minute of the hour to start
- `joinWindowSeconds` — how long players can join before round starts
- `questionCount` — number of questions per round
- `questionTimeSeconds` — time allowed to answer each question
- `tickEverySeconds` — interval for countdown messages
- `noRepeatWindow` — number of recent questions to avoid repeating
- `reward.credits` — credits given to winners
- `reward.consoleCommandTemplate` — console command to award credits (e.g., `"credits add {playerId} {amount}"`)
- `messages` — customize broadcast and private messages

## Commands
**Player Commands:**
- `/trivia join` — join the active round
- `/a <A|B|C|D>` — answer the current question
- `/claim` — claim reward if you won the last round
- `/trivia status` — show current game state

**Admin Commands:**
- `/trivia start` — start a round immediately (requires 3+ players)
- `/trivia dryrun` — start a test round (bypasses player minimum, no payouts)
- `/trivia stop` — abort the current round
- `/trivia reload` — reload config and questions
- `/trivia reward <credits>` — set reward amount for future rounds

## Dry Run Mode
- Starts immediately with the admin auto-joined
- Shows `[DRY-RUN: no payouts]` tag in all messages
- Allows full round flow without awarding credits

## Question Bank (`questions.json`)
- Contains an array of question objects:
```json
{
  "id": "unique-id",
  "cat": "gaming",
  "q": "Question text?",
  "choices": ["A1", "A2", "A3", "A4"],
  "answer": 0,
  "difficulty": 1
}
```
- Add or edit entries to customize the trivia content.

## Logging
- `trivia.log` records events, errors, and debug output with rotation support
- Configurable log levels for production vs development environments
- Automatic cleanup of old log files to prevent disk space issues

## Technical Architecture

### Framework Compatibility
- **Target Framework**: .NET Standard 2.0
- **Empyrion API**: Uses hybrid ModInterface + IMod pattern
- **Dependencies**: ModApi.dll, YamlDotNet for configuration, protobuf-net for game communication

### Performance Features
- **Efficient Scheduling**: Timer-based system with minimal CPU overhead
- **Memory Management**: Automatic cleanup of old game states
- **Concurrent Safe**: Thread-safe operations for multiplayer environments

## Contributing

Contributions welcome! Areas for enhancement:
- Additional question categories beyond gaming
- Multi-language support for international servers
- Advanced scoring algorithms (time-based bonuses, streaks)
- Integration with other server economy mods

## Changelog

See commit history for detailed changes and version updates.

---

*Developed with ❤️ for the Empyrion Galactic Survival modding community*
