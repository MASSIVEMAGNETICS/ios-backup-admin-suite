@echo off
REM iOS Backup Admin Suite - Windows Test Script
REM This script tests the Windows application functionality

setlocal enabledelayedexpansion

echo =========================================================
echo    iOS Backup Admin Suite - Windows Test Suite
echo =========================================================
echo.

REM Set up test environment
set "TEST_DIR=%TEMP%\ios-backup-test"
set "BACKUP_DIR=%TEST_DIR%\backup"
set "RESTORE_DIR=%TEST_DIR%\restore"
set "EXE_PATH=.build\release\ios-backup-windows.exe"

REM Check if executable exists
if not exist "%EXE_PATH%" (
    set "EXE_PATH=.build\debug\ios-backup-windows.exe"
)

if not exist "%EXE_PATH%" (
    echo ERROR: Executable not found. Please build the project first:
    echo   swift build
    exit /b 1
)

echo Using executable: %EXE_PATH%
echo.

REM Clean up previous test runs
if exist "%TEST_DIR%" (
    rmdir /s /q "%TEST_DIR%" 2>nul
)

REM Create test directories
mkdir "%TEST_DIR%" 2>nul
mkdir "%BACKUP_DIR%" 2>nul
mkdir "%RESTORE_DIR%" 2>nul

set PASSED=0
set FAILED=0

REM Test 1: Help Command
echo [TEST 1] Testing help command...
"%EXE_PATH%" help >nul 2>&1
if %errorLevel% equ 0 (
    echo [PASS] Help command works
    set /a PASSED+=1
) else (
    echo [FAIL] Help command failed
    set /a FAILED+=1
)
echo.

REM Test 2: List Devices Command
echo [TEST 2] Testing list-devices command...
"%EXE_PATH%" list-devices >nul 2>&1
if %errorLevel% equ 0 (
    echo [PASS] List devices command works
    set /a PASSED+=1
) else (
    echo [FAIL] List devices command failed
    set /a FAILED+=1
)
echo.

REM Test 3: Backup Command (creates manifest)
echo [TEST 3] Testing backup command...
"%EXE_PATH%" backup test-device "%BACKUP_DIR%" >nul 2>&1
if exist "%BACKUP_DIR%\manifest.json" (
    echo [PASS] Backup command created manifest
    set /a PASSED+=1
) else (
    echo [FAIL] Backup command did not create manifest
    set /a FAILED+=1
)
echo.

REM Test 4: Verify Command
echo [TEST 4] Testing verify command...
"%EXE_PATH%" verify "%BACKUP_DIR%" >nul 2>&1
if %errorLevel% equ 0 (
    echo [PASS] Verify command works
    set /a PASSED+=1
) else (
    echo [FAIL] Verify command failed
    set /a FAILED+=1
)
echo.

REM Test 5: Restore Command
echo [TEST 5] Testing restore command...
"%EXE_PATH%" restore "%BACKUP_DIR%" test-device >nul 2>&1
if %errorLevel% equ 0 (
    echo [PASS] Restore command works
    set /a PASSED+=1
) else (
    echo [FAIL] Restore command failed
    set /a FAILED+=1
)
echo.

REM Test 6: Encrypted Backup
echo [TEST 6] Testing encrypted backup...
"%EXE_PATH%" backup test-device-encrypted "%BACKUP_DIR%-encrypted" --encrypt "test-password" >nul 2>&1
if exist "%BACKUP_DIR%-encrypted\manifest.json" (
    echo [PASS] Encrypted backup created
    set /a PASSED+=1
) else (
    echo [FAIL] Encrypted backup failed
    set /a FAILED+=1
)
echo.

REM Test 7: Invalid Command
echo [TEST 7] Testing invalid command handling...
"%EXE_PATH%" invalid-command >nul 2>&1
if %errorLevel% neq 0 (
    echo [PASS] Invalid command properly rejected
    set /a PASSED+=1
) else (
    echo [FAIL] Invalid command should fail
    set /a FAILED+=1
)
echo.

REM Clean up test files
echo Cleaning up test files...
rmdir /s /q "%TEST_DIR%" 2>nul

REM Display results
echo =========================================================
echo                    Test Results
echo =========================================================
echo.
echo Tests Passed: %PASSED%
echo Tests Failed: %FAILED%
echo.

if %FAILED% equ 0 (
    echo [SUCCESS] All tests passed!
    echo.
    exit /b 0
) else (
    echo [FAILURE] Some tests failed!
    echo.
    exit /b 1
)
