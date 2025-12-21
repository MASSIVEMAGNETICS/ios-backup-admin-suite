@echo off
SETLOCAL EnableDelayedExpansion

echo ===================================================
echo OpenUltData Build Script
echo ===================================================
echo.
echo This script creates a clean Virtual Environment to ensure
echo the build is small and free of unrelated system libraries.
echo.

:: Check for Python
python --version >nul 2>&1
IF %ERRORLEVEL% NEQ 0 (
    echo Error: Python is not found in your PATH.
    echo Please install Python 3.10+ and check "Add to PATH".
    pause
    exit /b 1
)

:: Create VENV
if exist "venv" (
    echo [INFO] Virtual environment 'venv' already exists.
) else (
    echo [INFO] Creating virtual environment...
    python -m venv venv
)

:: Activate VENV
echo [INFO] Activating virtual environment...
call venv\Scripts\activate

:: Upgrade pip
python -m pip install --upgrade pip

:: Install CPU-only Torch (Saves ~2GB space)
echo [INFO] Installing PyTorch (CPU version for smaller size)...
pip install torch numpy --index-url https://download.pytorch.org/whl/cpu

:: Install other requirements
echo [INFO] Installing other dependencies...
pip install -r requirements.txt

:: Install PyInstaller
pip install pyinstaller

:: Build
echo [INFO] Building Executable...
echo This may take a few minutes.
pyinstaller --noconfirm --clean --onefile --windowed --name "OpenUltData" main.py

echo.
echo ===================================================
echo Build Complete!
echo You can find the executable in the 'dist' folder.
echo ===================================================
pause
