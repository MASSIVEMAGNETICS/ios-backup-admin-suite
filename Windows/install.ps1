# iOS Backup Admin Suite - Windows Installation Script
# This script installs the iOS Backup Windows Desktop Application

param(
    [string]$InstallPath = "$env:ProgramFiles\iOSBackupSuite",
    [switch]$SkipSwift = $false,
    [switch]$Help = $false
)

if ($Help) {
    Write-Host @"
iOS Backup Admin Suite - Windows Installer

Usage: .\install.ps1 [OPTIONS]

Options:
  -InstallPath <path>    Installation directory (default: C:\Program Files\iOSBackupSuite)
  -SkipSwift             Skip Swift installation check
  -Help                  Show this help message

Examples:
  .\install.ps1
  .\install.ps1 -InstallPath "C:\MyApps\iOSBackup"
  .\install.ps1 -SkipSwift

"@
    exit 0
}

Write-Host "╔═══════════════════════════════════════════════════════════╗" -ForegroundColor Cyan
Write-Host "║   iOS Backup Admin Suite - Windows Installer             ║" -ForegroundColor Cyan
Write-Host "╚═══════════════════════════════════════════════════════════╝" -ForegroundColor Cyan
Write-Host ""

# Check if running as administrator
$isAdmin = ([Security.Principal.WindowsPrincipal] [Security.Principal.WindowsIdentity]::GetCurrent()).IsInRole([Security.Principal.WindowsBuiltInRole]::Administrator)

if (-not $isAdmin) {
    Write-Host "⚠️  Warning: Not running as Administrator" -ForegroundColor Yellow
    Write-Host "   Installation to Program Files may require administrator privileges." -ForegroundColor Yellow
    Write-Host ""
}

# Step 1: Check Swift installation
if (-not $SkipSwift) {
    Write-Host "🔍 Checking Swift installation..." -ForegroundColor Cyan
    
    $swiftPath = Get-Command swift -ErrorAction SilentlyContinue
    
    if (-not $swiftPath) {
        Write-Host "❌ Swift is not installed on this system" -ForegroundColor Red
        Write-Host ""
        Write-Host "Please install Swift for Windows:" -ForegroundColor Yellow
        Write-Host "  1. Visit: https://www.swift.org/download/" -ForegroundColor Yellow
        Write-Host "  2. Download Swift for Windows" -ForegroundColor Yellow
        Write-Host "  3. Install and add to PATH" -ForegroundColor Yellow
        Write-Host ""
        Write-Host "Or use winget: winget install Swift.Toolchain" -ForegroundColor Yellow
        Write-Host ""
        exit 1
    }
    
    $swiftVersion = & swift --version 2>&1 | Select-Object -First 1
    Write-Host "✅ Swift found: $swiftVersion" -ForegroundColor Green
}

# Step 2: Create installation directory
Write-Host ""
Write-Host "📁 Creating installation directory..." -ForegroundColor Cyan
Write-Host "   Location: $InstallPath" -ForegroundColor Gray

try {
    if (-not (Test-Path $InstallPath)) {
        New-Item -ItemType Directory -Path $InstallPath -Force | Out-Null
        Write-Host "✅ Installation directory created" -ForegroundColor Green
    } else {
        Write-Host "✅ Installation directory already exists" -ForegroundColor Green
    }
} catch {
    Write-Host "❌ Failed to create installation directory: $_" -ForegroundColor Red
    exit 1
}

# Step 3: Build the application
Write-Host ""
Write-Host "🔨 Building iOS Backup Windows Application..." -ForegroundColor Cyan

$buildPath = Join-Path $PSScriptRoot ".."

try {
    Push-Location $buildPath
    
    Write-Host "   Running: swift build -c release" -ForegroundColor Gray
    $buildOutput = & swift build -c release 2>&1
    
    if ($LASTEXITCODE -ne 0) {
        Write-Host "❌ Build failed!" -ForegroundColor Red
        Write-Host $buildOutput
        Pop-Location
        exit 1
    }
    
    Write-Host "✅ Build completed successfully" -ForegroundColor Green
    Pop-Location
} catch {
    Write-Host "❌ Build error: $_" -ForegroundColor Red
    Pop-Location
    exit 1
}

# Step 4: Copy executable to installation directory
Write-Host ""
Write-Host "📦 Installing application..." -ForegroundColor Cyan

$exeName = "ios-backup-windows.exe"
$sourcePath = Join-Path $buildPath ".build\release\$exeName"

if (-not (Test-Path $sourcePath)) {
    # Try without .exe extension (Swift might not add it on all platforms)
    $sourcePathAlt = Join-Path $buildPath ".build\release\ios-backup-windows"
    if (Test-Path $sourcePathAlt) {
        $sourcePath = $sourcePathAlt
    } else {
        Write-Host "❌ Built executable not found at: $sourcePath" -ForegroundColor Red
        exit 1
    }
}

$destPath = Join-Path $InstallPath $exeName

try {
    Copy-Item -Path $sourcePath -Destination $destPath -Force
    Write-Host "✅ Application installed to: $destPath" -ForegroundColor Green
} catch {
    Write-Host "❌ Failed to copy executable: $_" -ForegroundColor Red
    exit 1
}

# Step 5: Add to PATH (optional)
Write-Host ""
Write-Host "🔧 Updating system PATH..." -ForegroundColor Cyan

$currentPath = [Environment]::GetEnvironmentVariable("Path", "User")

if ($currentPath -notlike "*$InstallPath*") {
    try {
        $newPath = "$currentPath;$InstallPath"
        [Environment]::SetEnvironmentVariable("Path", $newPath, "User")
        Write-Host "✅ Added $InstallPath to user PATH" -ForegroundColor Green
        Write-Host "   Please restart your terminal for PATH changes to take effect" -ForegroundColor Yellow
    } catch {
        Write-Host "⚠️  Could not update PATH automatically: $_" -ForegroundColor Yellow
        Write-Host "   You can manually add $InstallPath to your PATH" -ForegroundColor Yellow
    }
} else {
    Write-Host "✅ Installation directory already in PATH" -ForegroundColor Green
}

# Step 6: Create Start Menu shortcut (optional)
Write-Host ""
Write-Host "🔗 Creating Start Menu shortcut..." -ForegroundColor Cyan

$startMenuPath = "$env:APPDATA\Microsoft\Windows\Start Menu\Programs\iOSBackupSuite"

try {
    if (-not (Test-Path $startMenuPath)) {
        New-Item -ItemType Directory -Path $startMenuPath -Force | Out-Null
    }
    
    $shell = New-Object -ComObject WScript.Shell
    $shortcut = $shell.CreateShortcut("$startMenuPath\iOS Backup Windows.lnk")
    $shortcut.TargetPath = $destPath
    $shortcut.WorkingDirectory = $InstallPath
    $shortcut.Description = "iOS Backup Admin Suite for Windows"
    $shortcut.Save()
    
    Write-Host "✅ Start Menu shortcut created" -ForegroundColor Green
} catch {
    Write-Host "⚠️  Could not create Start Menu shortcut: $_" -ForegroundColor Yellow
}

# Step 7: Installation complete
Write-Host ""
Write-Host "╔═══════════════════════════════════════════════════════════╗" -ForegroundColor Green
Write-Host "║            Installation Completed Successfully!          ║" -ForegroundColor Green
Write-Host "╚═══════════════════════════════════════════════════════════╝" -ForegroundColor Green
Write-Host ""
Write-Host "📋 Installation Summary:" -ForegroundColor Cyan
Write-Host "   Location: $InstallPath" -ForegroundColor White
Write-Host "   Executable: $exeName" -ForegroundColor White
Write-Host ""
Write-Host "🚀 Quick Start:" -ForegroundColor Cyan
Write-Host "   1. Open a new terminal (to load updated PATH)" -ForegroundColor White
Write-Host "   2. Run: ios-backup-windows.exe help" -ForegroundColor White
Write-Host "   3. List devices: ios-backup-windows.exe list-devices" -ForegroundColor White
Write-Host "   4. Create backup: ios-backup-windows.exe backup <device-id> <output-path>" -ForegroundColor White
Write-Host ""
Write-Host "📚 Documentation: https://github.com/MASSIVEMAGNETICS/ios-backup-admin-suite" -ForegroundColor Cyan
Write-Host ""
