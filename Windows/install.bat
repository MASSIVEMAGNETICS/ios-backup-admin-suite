@echo off
REM iOS Backup Admin Suite - Windows Installation Batch Script
REM This is a simple alternative to the PowerShell installer

setlocal enabledelayedexpansion

echo =========================================================
echo    iOS Backup Admin Suite - Windows Installer
echo =========================================================
echo.

REM Check for Administrator privileges
net session >nul 2>&1
if %errorLevel% neq 0 (
    echo WARNING: Not running as Administrator
    echo Installation to Program Files may require admin privileges.
    echo.
)

REM Set installation directory
set "INSTALL_DIR=%ProgramFiles%\iOSBackupSuite"

echo Installation directory: %INSTALL_DIR%
echo.

REM Check if Swift is installed
echo Checking Swift installation...
where swift >nul 2>&1
if %errorLevel% neq 0 (
    echo ERROR: Swift is not installed!
    echo.
    echo Please install Swift for Windows:
    echo   1. Visit: https://www.swift.org/download/
    echo   2. Or use: winget install Swift.Toolchain
    echo.
    pause
    exit /b 1
)

for /f "tokens=*" %%i in ('swift --version 2^>^&1 ^| findstr /i "Swift"') do set SWIFT_VERSION=%%i
echo Swift found: %SWIFT_VERSION%
echo.

REM Create installation directory
echo Creating installation directory...
if not exist "%INSTALL_DIR%" (
    mkdir "%INSTALL_DIR%"
    if %errorLevel% neq 0 (
        echo ERROR: Failed to create installation directory
        pause
        exit /b 1
    )
)
echo Installation directory ready
echo.

REM Build the application
echo Building iOS Backup Windows Application...
echo This may take a few minutes...
cd /d "%~dp0.."
swift build -c release
if %errorLevel% neq 0 (
    echo ERROR: Build failed!
    pause
    exit /b 1
)
echo Build completed successfully
echo.

REM Copy executable
echo Installing application...
set "EXE_NAME=ios-backup-windows.exe"
set "SOURCE_PATH=.build\release\%EXE_NAME%"

if not exist "%SOURCE_PATH%" (
    REM Try without .exe extension
    set "SOURCE_PATH=.build\release\ios-backup-windows"
)

if not exist "%SOURCE_PATH%" (
    echo ERROR: Built executable not found
    pause
    exit /b 1
)

copy "%SOURCE_PATH%" "%INSTALL_DIR%\%EXE_NAME%" >nul
if %errorLevel% neq 0 (
    echo ERROR: Failed to copy executable
    pause
    exit /b 1
)
echo Application installed to: %INSTALL_DIR%\%EXE_NAME%
echo.

REM Add to PATH
echo Updating PATH...
setx PATH "%PATH%;%INSTALL_DIR%" >nul 2>&1
if %errorLevel% equ 0 (
    echo PATH updated successfully
    echo Please restart your terminal for changes to take effect
) else (
    echo WARNING: Could not update PATH automatically
    echo Please manually add %INSTALL_DIR% to your PATH
)
echo.

REM Installation complete
echo =========================================================
echo           Installation Completed Successfully!
echo =========================================================
echo.
echo Installation Summary:
echo   Location: %INSTALL_DIR%
echo   Executable: %EXE_NAME%
echo.
echo Quick Start:
echo   1. Open a new terminal
echo   2. Run: ios-backup-windows.exe help
echo   3. List devices: ios-backup-windows.exe list-devices
echo.
echo Press any key to exit...
pause >nul
