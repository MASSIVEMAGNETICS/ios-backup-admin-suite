# iOS Backup Admin Suite - Windows Test Suite (PowerShell)
# This script tests the Windows application functionality

param(
    [switch]$Verbose = $false
)

Write-Host "╔═══════════════════════════════════════════════════════════╗" -ForegroundColor Cyan
Write-Host "║   iOS Backup Admin Suite - Windows Test Suite            ║" -ForegroundColor Cyan
Write-Host "╚═══════════════════════════════════════════════════════════╝" -ForegroundColor Cyan
Write-Host ""

# Set up test environment
$testDir = Join-Path $env:TEMP "ios-backup-test"
$backupDir = Join-Path $testDir "backup"
$restoreDir = Join-Path $testDir "restore"
$exePath = ".build\release\ios-backup-windows.exe"

# Check if executable exists
if (-not (Test-Path $exePath)) {
    $exePath = ".build\debug\ios-backup-windows.exe"
}

if (-not (Test-Path $exePath)) {
    Write-Host "❌ ERROR: Executable not found. Please build the project first:" -ForegroundColor Red
    Write-Host "   swift build" -ForegroundColor Yellow
    exit 1
}

Write-Host "📦 Using executable: $exePath" -ForegroundColor Gray
Write-Host ""

# Clean up previous test runs
if (Test-Path $testDir) {
    Remove-Item -Path $testDir -Recurse -Force -ErrorAction SilentlyContinue
}

# Create test directories
New-Item -ItemType Directory -Path $testDir -Force | Out-Null
New-Item -ItemType Directory -Path $backupDir -Force | Out-Null
New-Item -ItemType Directory -Path $restoreDir -Force | Out-Null

$passed = 0
$failed = 0
$testNumber = 0

function Run-Test {
    param(
        [string]$Name,
        [scriptblock]$Test
    )
    
    $script:testNumber++
    Write-Host "[TEST $testNumber] $Name..." -ForegroundColor Cyan
    
    try {
        $result = & $Test
        if ($result) {
            Write-Host "[PASS] $Name" -ForegroundColor Green
            $script:passed++
        } else {
            Write-Host "[FAIL] $Name" -ForegroundColor Red
            $script:failed++
        }
    } catch {
        Write-Host "[FAIL] $Name - Exception: $_" -ForegroundColor Red
        if ($Verbose) {
            Write-Host $_.ScriptStackTrace -ForegroundColor Gray
        }
        $script:failed++
    }
    Write-Host ""
}

# Test 1: Help Command
Run-Test "Help command" {
    $output = & $exePath help 2>&1
    return $LASTEXITCODE -eq 0 -and $output -match "Usage:"
}

# Test 2: List Devices Command
Run-Test "List devices command" {
    $output = & $exePath list-devices 2>&1
    return $LASTEXITCODE -eq 0
}

# Test 3: Backup Command
Run-Test "Backup command creates manifest" {
    $output = & $exePath backup test-device $backupDir 2>&1
    $manifestPath = Join-Path $backupDir "manifest.json"
    return (Test-Path $manifestPath)
}

# Test 4: Manifest Content
Run-Test "Manifest contains valid JSON" {
    $manifestPath = Join-Path $backupDir "manifest.json"
    if (Test-Path $manifestPath) {
        $content = Get-Content $manifestPath -Raw
        $json = $content | ConvertFrom-Json
        return $json.device_id -eq "test-device"
    }
    return $false
}

# Test 5: Verify Command
Run-Test "Verify command" {
    $output = & $exePath verify $backupDir 2>&1
    return $LASTEXITCODE -eq 0 -and $output -match "verification completed"
}

# Test 6: Restore Command
Run-Test "Restore command" {
    $output = & $exePath restore $backupDir test-device 2>&1
    return $LASTEXITCODE -eq 0
}

# Test 7: Encrypted Backup
Run-Test "Encrypted backup" {
    $encBackupDir = "$backupDir-encrypted"
    New-Item -ItemType Directory -Path $encBackupDir -Force | Out-Null
    $output = & $exePath backup test-device-encrypted $encBackupDir --encrypt "test-password" 2>&1
    $manifestPath = Join-Path $encBackupDir "manifest.json"
    return (Test-Path $manifestPath)
}

# Test 8: Invalid Command Handling
Run-Test "Invalid command handling" {
    $output = & $exePath invalid-command 2>&1
    return $LASTEXITCODE -ne 0
}

# Test 9: Missing Arguments
Run-Test "Missing arguments handling" {
    $output = & $exePath backup 2>&1
    return $LASTEXITCODE -ne 0 -or $output -match "Missing arguments"
}

# Test 10: Verify Non-existent Backup
Run-Test "Verify non-existent backup" {
    $nonExistentPath = Join-Path $env:TEMP "NonExistentBackup-$(Get-Random)"
    $output = & $exePath verify $nonExistentPath 2>&1
    return $output -match "not found"
}

# Clean up test files
Write-Host "🧹 Cleaning up test files..." -ForegroundColor Gray
Remove-Item -Path $testDir -Recurse -Force -ErrorAction SilentlyContinue

# Display results
Write-Host ""
Write-Host "╔═══════════════════════════════════════════════════════════╗" -ForegroundColor Cyan
Write-Host "║                    Test Results                           ║" -ForegroundColor Cyan
Write-Host "╚═══════════════════════════════════════════════════════════╝" -ForegroundColor Cyan
Write-Host ""
Write-Host "Tests Passed: " -NoNewline
Write-Host "$passed" -ForegroundColor Green
Write-Host "Tests Failed: " -NoNewline
Write-Host "$failed" -ForegroundColor $(if ($failed -eq 0) { "Green" } else { "Red" })
Write-Host "Total Tests:  $($passed + $failed)" -ForegroundColor White
Write-Host ""

if ($failed -eq 0) {
    Write-Host "╔═══════════════════════════════════════════════════════════╗" -ForegroundColor Green
    Write-Host "║            SUCCESS - All tests passed! ✓                 ║" -ForegroundColor Green
    Write-Host "╚═══════════════════════════════════════════════════════════╝" -ForegroundColor Green
    Write-Host ""
    exit 0
} else {
    Write-Host "╔═══════════════════════════════════════════════════════════╗" -ForegroundColor Red
    Write-Host "║            FAILURE - Some tests failed! ✗                ║" -ForegroundColor Red
    Write-Host "╚═══════════════════════════════════════════════════════════╝" -ForegroundColor Red
    Write-Host ""
    exit 1
}
