@echo off
REM One-Click Smart Setup for iOS Backup Admin Suite
REM Wraps the Windows installation logic

echo =========================================================
echo    iOS Backup Admin Suite - Smart Setup
echo =========================================================
echo.

REM Navigate to Windows directory if running from root
if exist "Windows\install.bat" (
    cd Windows
)

REM Check if install.bat exists in current directory
if exist "install.bat" (
    echo Launching installer...
    call install.bat
) else (
    echo ERROR: Could not find Windows/install.bat
    echo Please ensure you are running this script from the repository root.
    pause
    exit /b 1
)

pause
