@echo off
SETLOCAL EnableDelayedExpansion

echo ===================================================
echo OpenUltData Build Script
echo ===================================================
echo.
echo This script creates a clean Virtual Environment to ensure
echo the build is small and free of unrelated system libraries.
echo.

:: Check for main.py
if not exist "main.py" (
    echo [ERROR] main.py not found in the current directory!
    echo Please make sure you have the source code files.
    pause
    exit /b 1
)

:: Check for Python
python --version >nul 2>&1
IF %ERRORLEVEL% NEQ 0 (
    echo Error: Python is not found in your PATH.
    echo Please install Python 3.10+ and check "Add to PATH".
    pause
    exit /b 1
)

:: Clean previous build artifacts and venv
echo [INFO] Cleaning up previous build environments...
if exist "venv" (
    echo [INFO] Deleting old virtual environment...
    rmdir /s /q venv
)
if exist "build" rmdir /s /q build
if exist "dist" rmdir /s /q dist
if exist "OpenUltData.spec" del OpenUltData.spec

:: Create VENV
echo [INFO] Creating new virtual environment...
python -m venv venv

:: Activate VENV
echo [INFO] Activating virtual environment...
call venv\Scripts\activate

:: Upgrade pip
python -m pip install --upgrade pip

:: Install CPU-only Torch (Saves ~2GB space)
echo [INFO] Installing PyTorch (CPU version for smaller size)...
pip install torch numpy<2.0.0 --index-url https://download.pytorch.org/whl/cpu

:: Install other requirements
echo [INFO] Installing other dependencies...
pip install -r requirements.txt

:: Install PyInstaller
pip install pyinstaller

:: Build
echo [INFO] Building Executable...
echo This may take a few minutes.
:: We explicitly exclude heavy data science libraries that might be pulled in by mistake
pyinstaller --noconfirm --clean --onefile --windowed --name "OpenUltData" ^
    --exclude-module nltk ^
    --exclude-module scipy ^
    --exclude-module matplotlib ^
    --exclude-module pandas ^
    --exclude-module tkinter ^
    main.py

echo.
echo ===================================================
echo Build Complete!
echo You can find the executable in the 'dist' folder.
echo ===================================================
pause
