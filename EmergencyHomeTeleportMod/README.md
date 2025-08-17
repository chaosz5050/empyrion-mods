# EmergencyHomeTeleport Mod

Emergency home teleportation system for Empyrion Galactic Survival servers. Perfect for getting out of dangerous situations when you're stuck in space or need to quickly return to safety.

## Features

- **Set Home**: `/sethome` - Set your home location with safety height offset
- **Emergency Teleport**: `/home` - Cross-playfield teleportation with confirmation dialog
- **Usage Tracking**: `/home uses` - Check remaining daily uses
- **Two-Button Dialog**: Visual popup with "✅ Teleport Now" and "❌ Cancel" buttons
- **Daily Limits**: Configurable uses per 24 hours (default: 3)
- **Vehicle Teleportation**: Teleports both player AND current vehicle together!
- **Cross-Playfield Support**: Works from space to planet, planet to planet, etc.
- **Health Restoration**: Restores full health after teleport to prevent damage
- **Safe Positioning**: 5-meter height safety offset prevents underground burial

## Installation

1. Build the mod:
   ```bash
   dotnet restore
   dotnet build -c Release
   ```

2. Copy the built DLL to your server's mod directory:
   - `bin/Debug/netstandard2.0/EmergencyHomeTeleport.dll`

## Configuration

The mod creates a `config.json` file in the mod's Data directory:

```json
{
  "MaxTeleportsPer24h": 3
}
```

Valid range: 1-24 teleports per 24-hour period.

## Data Storage

Player home data is stored in `EmergencyHomeTeleport/homes.json` with:
- Home position and playfield (with 3m safety height offset)
- Usage timestamps for the rolling 24-hour window
- Per-player usage tracking

Configuration is stored in `EmergencyHomeTeleport/config.json`.

## Commands

- `/sethome` - Set your current location as home (adds 3m safety height)
- `/home` - Shows confirmation dialog with "Teleport Now" and "Cancel" buttons
- `/home uses` - Check remaining teleports for today

## How It Works

1. Use `/sethome` while standing where you want your emergency escape point
2. When in trouble, use `/home` to get a confirmation dialog
3. Click "✅ Teleport Now" to instantly teleport with your vehicle, or "❌ Cancel" to abort
4. You and your current vehicle will be teleported to your home location
5. Your health will be restored after arrival
6. Uses one of your daily teleport allowances

## Technical Details

- Built for .NET Standard 2.0
- Uses hybrid ModInterface + IModApi architecture
- Cross-playfield teleportation via `Request_Player_ChangePlayerfield`
- Same-playfield fallback via `Request_Entity_Teleport`
- Two-button dialogs using `DialogBoxData` with `PosButtonText`/`NegButtonText`
- Real-time player position capture with safety height calculations
- JSON persistence with automatic validation and error handling
