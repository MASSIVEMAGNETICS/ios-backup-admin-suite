# Windows 10 Desktop Application - Quick Reference

## Overview

The iOS Backup Admin Suite now includes a native Windows 10 desktop application that serves as the **Companion Desktop Agent** described in the main architecture (README.md).

## Platform Support

| Platform | Status | Features |
|----------|--------|----------|
| **Windows 10+** | ✅ Supported | Full backup/restore, encryption, verification |
| **macOS 12+** | ✅ Supported | PhotoKit, full features |
| **iOS 15+** | ✅ Supported | PhotoKit, on-device backup |
| **Linux** | ✅ Supported | Restore and verification |

## Quick Installation (Windows)

### Prerequisites
- Windows 10 (1809+) or Windows 11
- Swift 5.7 or later: `winget install Swift.Toolchain`

### Install

```powershell
# Clone repository
git clone https://github.com/MASSIVEMAGNETICS/ios-backup-admin-suite.git
cd ios-backup-admin-suite

# Run installer
cd Windows
.\install.ps1
```

### Verify Installation

```cmd
ios-backup-windows.exe help
```

## Quick Usage (Windows)

```cmd
# List connected devices
ios-backup-windows.exe list-devices

# Create backup
ios-backup-windows.exe backup my-iphone C:\Backups\iPhone

# Create encrypted backup
ios-backup-windows.exe backup my-iphone C:\Backups\iPhone --encrypt "MyPassword"

# Verify backup
ios-backup-windows.exe verify C:\Backups\iPhone

# Restore backup
ios-backup-windows.exe restore C:\Backups\iPhone my-iphone
```

## Documentation

- **[Windows Setup Guide](Windows/SETUP-WINDOWS.md)** - Complete Windows installation and usage
- **[Windows README](Windows/README.md)** - Windows directory overview
- **[QUICKSTART.md](QUICKSTART.md)** - General quick start guide
- **[README.md](README.md)** - Full architecture and design

## Architecture Integration

The Windows desktop application implements the **Companion Desktop Agent** component from the main architecture:

```
┌─────────────────────────────────────────────────────────────┐
│                   iOS Backup Admin Suite                    │
├─────────────────────────────────────────────────────────────┤
│                                                             │
│  ┌──────────────┐    USB     ┌────────────────────────┐   │
│  │              │◄───────────►│  Windows Desktop App   │   │
│  │  iOS Device  │             │  (Companion Agent)     │   │
│  │              │             │                        │   │
│  └──────────────┘             │  - Full backups        │   │
│                               │  - Encryption          │   │
│                               │  - Deduplication       │   │
│                               │  - Verification        │   │
│                               └────────────────────────┘   │
│                                           │                 │
│                                           ▼                 │
│                               ┌────────────────────────┐   │
│                               │   Backup Storage       │   │
│                               │   - Local drives       │   │
│                               │   - External USB       │   │
│                               │   - Network shares     │   │
│                               └────────────────────────┘   │
└─────────────────────────────────────────────────────────────┘
```

## Features

✅ **Cross-Platform**: Swift-based, runs natively on Windows 10+  
✅ **Command-Line Interface**: Easy automation and scripting  
✅ **Encryption**: AES-256-GCM for secure backups  
✅ **Verification**: SHA-256 checksums and Merkle trees  
✅ **Easy Installation**: PowerShell and batch installers  
✅ **Testing**: Comprehensive test suites included  

## Testing

```powershell
# Run tests
cd Windows
.\test.ps1

# Run with verbose output
.\test.ps1 -Verbose
```

## Building from Source

```powershell
# Build debug
swift build

# Build release
swift build -c release

# Run tests
swift test
```

## Project Structure

```
ios-backup-admin-suite/
├── Sources/
│   ├── IOSBackupKit/          # Core library (cross-platform)
│   ├── WindowsApp/            # Windows desktop application
│   └── RestoreTool/           # CLI restore tool
├── Windows/                   # Windows-specific files
│   ├── install.ps1           # PowerShell installer
│   ├── install.bat           # Batch installer
│   ├── test.ps1              # PowerShell tests
│   ├── test.bat              # Batch tests
│   ├── SETUP-WINDOWS.md      # Complete Windows guide
│   └── README.md             # Windows directory docs
└── Package.swift             # Swift Package Manager config
```

## Next Steps

1. **Install**: Follow [Windows Setup Guide](Windows/SETUP-WINDOWS.md)
2. **Test**: Run the test suite to verify installation
3. **Use**: Create your first backup
4. **Integrate**: Add libimobiledevice for full device support

## Support

- **Issues**: https://github.com/MASSIVEMAGNETICS/ios-backup-admin-suite/issues
- **Architecture**: See main [README.md](README.md)
- **Windows Help**: See [SETUP-WINDOWS.md](Windows/SETUP-WINDOWS.md)
