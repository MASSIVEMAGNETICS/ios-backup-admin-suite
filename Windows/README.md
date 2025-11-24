# Windows Desktop Application

This directory contains the Windows 10 desktop application for the iOS Backup Admin Suite, along with installation and testing scripts.

## Contents

- **`install.ps1`** - PowerShell installation script (recommended)
- **`install.bat`** - Batch file installer (alternative)
- **`test.ps1`** - PowerShell test suite
- **`test.bat`** - Batch file test suite
- **`SETUP-WINDOWS.md`** - Comprehensive Windows setup guide

## Quick Start

### Installation

#### Using PowerShell (Recommended)

```powershell
cd Windows
.\install.ps1
```

#### Using Batch File

```cmd
cd Windows
install.bat
```

### Testing

After installation, run the test suite:

#### Using PowerShell

```powershell
cd Windows
.\test.ps1
```

#### Using Batch File

```cmd
cd Windows
test.bat
```

## Prerequisites

- Windows 10 (version 1809+) or Windows 11
- Swift 5.7 or later
- 4 GB RAM minimum (8 GB recommended)

## Installation Options

The PowerShell installer supports several options:

```powershell
# Custom installation path
.\install.ps1 -InstallPath "C:\CustomPath"

# Skip Swift version check
.\install.ps1 -SkipSwift

# Show help
.\install.ps1 -Help
```

## Testing Options

The PowerShell test suite supports verbose output:

```powershell
# Run tests with verbose output
.\test.ps1 -Verbose
```

## Files Generated During Installation

- `C:\Program Files\iOSBackupSuite\ios-backup-windows.exe` - Main executable
- Start Menu shortcut in `%APPDATA%\Microsoft\Windows\Start Menu\Programs\iOSBackupSuite`
- Updated PATH environment variable

## Building Manually

If you prefer to build manually instead of using the installers:

```powershell
# From the repository root
swift build -c release
```

The executable will be created at:
```
.build\release\ios-backup-windows.exe
```

## Usage Examples

After installation:

```cmd
# Show help
ios-backup-windows.exe help

# List connected devices
ios-backup-windows.exe list-devices

# Create a backup
ios-backup-windows.exe backup my-iphone C:\Backups\iPhone

# Create encrypted backup
ios-backup-windows.exe backup my-iphone C:\Backups\iPhone --encrypt "password"

# Verify backup
ios-backup-windows.exe verify C:\Backups\iPhone

# Restore backup
ios-backup-windows.exe restore C:\Backups\iPhone my-iphone
```

## Documentation

For detailed setup instructions, troubleshooting, and usage guides, see:

- **[SETUP-WINDOWS.md](SETUP-WINDOWS.md)** - Complete Windows setup guide
- **[../QUICKSTART.md](../QUICKSTART.md)** - Quick start guide
- **[../README.md](../README.md)** - Architecture and design

## Troubleshooting

### Common Issues

1. **"Swift is not installed"**
   - Install Swift from [swift.org](https://www.swift.org/download/) or use `winget install Swift.Toolchain`

2. **"Access Denied" during installation**
   - Run PowerShell or Command Prompt as Administrator

3. **PATH not updated**
   - Restart your terminal or manually add `C:\Program Files\iOSBackupSuite` to PATH

4. **Tests fail**
   - Ensure the application is built: `swift build`
   - Check that Swift is properly installed

For more troubleshooting help, see [SETUP-WINDOWS.md](SETUP-WINDOWS.md).

## Support

- **Issues**: https://github.com/MASSIVEMAGNETICS/ios-backup-admin-suite/issues
- **Documentation**: See SETUP-WINDOWS.md for comprehensive guide

## License

See repository root for license information.
