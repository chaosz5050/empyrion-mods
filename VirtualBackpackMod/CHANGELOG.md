# VirtualBackpackMod Changelog

## Version 2.0.1 (2025-08-16)

### Fixed
- **Doomsday Backup Protection**: `.bak3` files are now time-protected and only overwritten if they're at least 24 hours old
- **Data Loss Prevention**: Prevents rapid backup rotation from wiping out all good backups during problematic save scenarios
- **Enhanced Logging**: Added detailed logging for backup preservation decisions

### Technical Details
- `.bak3` files now serve as "doomsday backups" that survive at least one full day
- Backup rotation logic enhanced with age checking using `File.GetLastWriteTime()`
- Graceful fallback when `.bak3` is too recent to overwrite
- Console logging shows backup age and promotion decisions for debugging

---

## Version 2.0.0 (2025-08-16)

### Added
- **VB6 Support**: Added sixth virtual backpack accessible via `/vb6`
- **Starter Kit Integration**: VB6 pre-loads with configurable starter items on first access
- **Credit Rewards**: Automatic credit rewards for first-time VB6 users
- **StarterKitContents.json**: Configuration file for customizing starter kit items and rewards
- **Admin VB6 Support**: Extended `/vbopen` command to support slot 6 (`/vbopen <playerId> <1-6>`)

### Changed
- **Version**: Updated from 1.0.0 to 2.0.0
- **Slot Range**: Admin commands now support slots 1-6 instead of 1-5
- **Documentation**: Updated README.md with VB6 and starter kit information

### Technical Details
- VB6 functions as a normal persistent backpack after first use
- Starter kit loading includes proper `slotIdx` assignment and safety fallbacks
- Credit rewards executed via configurable console command template
- Full backward compatibility with existing VB1-5 functionality
- Atomic saves and backup rotation extended to VB6

---

## Version 1.0.0

### Initial Features
- Five virtual backpacks (`/vb1` through `/vb5`)
- Persistent per-player storage with JSON serialization
- Atomic saves with verification and backup rotation
- Remote admin access via `/vbopen` command
- Admin permission checking and locking system
- Automatic data recovery from backup files
- Comprehensive error handling and logging