# iOS Backup Admin Suite - Windows 10 Setup Guide

## Overview

This guide will help you set up and use the iOS Backup Admin Suite Windows Desktop Application on Windows 10.

## System Requirements

- **Operating System**: Windows 10 (version 1809 or later) or Windows 11
- **Architecture**: x64 (64-bit)
- **Memory**: Minimum 4 GB RAM (8 GB recommended)
- **Disk Space**: At least 2 GB free space for installation
- **Swift Runtime**: Swift 5.7 or later

## Prerequisites

### 1. Install Swift for Windows

The application requires Swift runtime to be installed on your system.

#### Option A: Using winget (Recommended)

```powershell
winget install Swift.Toolchain
```

#### Option B: Manual Installation

1. Visit [Swift.org Downloads](https://www.swift.org/download/)
2. Download the latest Swift toolchain for Windows
3. Run the installer
4. Follow the installation wizard
5. Ensure Swift is added to your system PATH

#### Verify Swift Installation

Open PowerShell or Command Prompt and run:

```powershell
swift --version
```

You should see output similar to:
```
Swift version 5.9.2 (swift-5.9.2-RELEASE)
Target: x86_64-unknown-windows-msvc
```

### 2. Install Git (Optional, for building from source)

If you plan to build from source:

```powershell
winget install Git.Git
```

Or download from: https://git-scm.com/download/win

## Installation

### Method 1: Using PowerShell Installer (Recommended)

1. Open PowerShell as Administrator
2. Navigate to the project directory
3. Run the installation script:

```powershell
cd Windows
.\install.ps1
```

#### Installation Options

```powershell
# Custom installation path
.\install.ps1 -InstallPath "C:\MyApps\iOSBackup"

# Skip Swift version check
.\install.ps1 -SkipSwift

# Show help
.\install.ps1 -Help
```

### Method 2: Using Batch File

1. Open Command Prompt as Administrator
2. Navigate to the project directory
3. Run:

```cmd
cd Windows
install.bat
```

### Method 3: Manual Installation

1. Build the application:

```powershell
swift build -c release
```

2. Copy the executable:

```powershell
copy .build\release\ios-backup-windows.exe "C:\Program Files\iOSBackupSuite\"
```

3. Add to PATH:

```powershell
setx PATH "%PATH%;C:\Program Files\iOSBackupSuite"
```

## Usage

### Basic Commands

#### Show Help

```cmd
ios-backup-windows.exe help
```

#### List Connected iOS Devices

```cmd
ios-backup-windows.exe list-devices
```

**Note**: Device detection requires libimobiledevice. See [Additional Setup](#additional-setup) below.

#### Create a Backup

```cmd
ios-backup-windows.exe backup <device-id> <output-path>
```

Example:
```cmd
ios-backup-windows.exe backup my-iphone "C:\Backups\iPhone"
```

#### Create an Encrypted Backup

```cmd
ios-backup-windows.exe backup <device-id> <output-path> --encrypt "my-password"
```

Example:
```cmd
ios-backup-windows.exe backup my-iphone "C:\Backups\iPhone" --encrypt "MySecurePassword123"
```

#### Restore a Backup

```cmd
ios-backup-windows.exe restore <backup-path> <device-id>
```

Example:
```cmd
ios-backup-windows.exe restore "C:\Backups\iPhone" my-iphone
```

#### Restore an Encrypted Backup

```cmd
ios-backup-windows.exe restore <backup-path> <device-id> --decrypt "my-password"
```

#### Verify Backup Integrity

```cmd
ios-backup-windows.exe verify <backup-path>
```

Example:
```cmd
ios-backup-windows.exe verify "C:\Backups\iPhone"
```

## Additional Setup

### Installing libimobiledevice for Full Device Support

For full iOS device detection and backup capabilities, install libimobiledevice:

#### Using winget

```powershell
winget install libimobiledevice
```

#### Manual Installation

1. Download from: https://github.com/libimobiledevice-win32/imobiledevice-net
2. Extract to a directory (e.g., `C:\libimobiledevice`)
3. Add the bin directory to your PATH

#### Verify Installation

```cmd
idevice_id -l
```

This should list connected iOS devices.

## Building from Source

### Clone the Repository

```powershell
git clone https://github.com/MASSIVEMAGNETICS/ios-backup-admin-suite.git
cd ios-backup-admin-suite
```

### Build Debug Version

```powershell
swift build
```

### Build Release Version

```powershell
swift build -c release
```

### Run Tests

```powershell
swift test
```

### Locate the Built Executable

```powershell
# Debug build
.\.build\debug\ios-backup-windows.exe

# Release build
.\.build\release\ios-backup-windows.exe
```

## Configuration

### Backup Location

By default, backups are stored in the specified output path. Example structure:

```
C:\Backups\iPhone\
├── manifest.json         # Backup metadata
├── commit.log           # Write-ahead log
├── objects/             # Encrypted chunks
│   ├── a3b1c2d3.chunk
│   ├── e4f5g6h7.chunk
│   └── ...
└── files/               # File metadata
    ├── photo1.json
    └── photo2.json
```

### Encryption

The application uses AES-256-GCM encryption for secure backups. When using encryption:

1. Choose a strong passphrase (at least 12 characters)
2. Store your passphrase securely
3. Without the passphrase, backups cannot be restored

## Troubleshooting

### "Swift is not installed"

**Solution**: Install Swift using winget or from swift.org as described in [Prerequisites](#prerequisites).

### "Device not found"

**Possible causes**:
1. iOS device is not connected via USB
2. libimobiledevice is not installed
3. iTunes/Apple Mobile Device Support is not installed

**Solutions**:
1. Connect your iOS device via USB cable
2. Install libimobiledevice (see [Additional Setup](#additional-setup))
3. Install iTunes from Microsoft Store or Apple

### "Access Denied" during installation

**Solution**: Run PowerShell or Command Prompt as Administrator.

### "Backup failed: Permission denied"

**Solution**: Ensure you have write permissions to the backup destination directory.

### "Module 'Crypto' not found"

**Solution**: The required dependencies are not installed. Build the project with:

```powershell
swift build -c release
```

This will automatically download and build dependencies.

### PATH not updated after installation

**Solution**: 
1. Restart your terminal
2. Or manually add to PATH:
   ```powershell
   setx PATH "%PATH%;C:\Program Files\iOSBackupSuite"
   ```

### Application won't start

**Verify**:
1. Swift is installed and in PATH
2. All DLL dependencies are present
3. Windows Defender hasn't quarantined the executable

## Performance Tips

1. **Use SSD Storage**: Store backups on SSD for better performance
2. **USB 3.0**: Use USB 3.0 ports for faster data transfer
3. **Encrypt Later**: For faster backups, encrypt after creation using separate tools
4. **Batch Operations**: When backing up multiple devices, process them sequentially

## Security Best Practices

1. **Encrypt Sensitive Data**: Always use encryption for backups containing personal data
2. **Secure Passphrases**: Use strong, unique passphrases for encrypted backups
3. **Backup the Backup**: Keep multiple copies of important backups
4. **Verify Integrity**: Regularly verify backup integrity using the `verify` command
5. **Secure Storage**: Store backups on encrypted drives or secure cloud storage

## Uninstallation

### Using PowerShell

```powershell
Remove-Item -Path "$env:ProgramFiles\iOSBackupSuite" -Recurse -Force
```

### Manually

1. Delete installation directory: `C:\Program Files\iOSBackupSuite`
2. Remove from PATH (System Properties → Environment Variables)
3. Delete Start Menu shortcuts (optional)

## Advanced Usage

### Automated Backups

Create a scheduled task using Task Scheduler:

```powershell
# Create a scheduled backup task
$action = New-ScheduledTaskAction -Execute "ios-backup-windows.exe" -Argument "backup my-iphone C:\Backups\iPhone --encrypt MyPassword"
$trigger = New-ScheduledTaskTrigger -Daily -At 2am
Register-ScheduledTask -TaskName "iOS Daily Backup" -Action $action -Trigger $trigger
```

### Backup Multiple Devices

Create a batch script:

```batch
@echo off
ios-backup-windows.exe backup device1 C:\Backups\Device1 --encrypt %PASSWORD%
ios-backup-windows.exe backup device2 C:\Backups\Device2 --encrypt %PASSWORD%
echo Backup completed!
```

## Support and Documentation

- **Project Repository**: https://github.com/MASSIVEMAGNETICS/ios-backup-admin-suite
- **Issues**: https://github.com/MASSIVEMAGNETICS/ios-backup-admin-suite/issues
- **Architecture**: See [README.md](../README.md)
- **Quick Start**: See [QUICKSTART.md](../QUICKSTART.md)
- **Implementation**: See [IMPLEMENTATION.md](../IMPLEMENTATION.md)

## License

See repository for license information.

## Changelog

### Version 1.0.0
- Initial Windows 10 desktop application release
- Basic backup and restore functionality
- AES-256-GCM encryption support
- PowerShell and batch installers
- Command-line interface
