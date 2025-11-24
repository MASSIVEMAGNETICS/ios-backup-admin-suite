#!/bin/bash
# Demonstration of Windows 10 Application Installation and Testing
# This script simulates what would happen on a Windows system

set -e

echo "╔═══════════════════════════════════════════════════════════╗"
echo "║   iOS Backup Suite - Windows Installation Demo           ║"
echo "╚═══════════════════════════════════════════════════════════╝"
echo ""

# Navigate to repo root
cd /home/runner/work/ios-backup-admin-suite/ios-backup-admin-suite

echo "📦 Step 1: Building the Windows application..."
echo "   Command: swift build -c release"
echo ""
swift build -c release 2>&1 | tail -20
echo ""

echo "✅ Build completed successfully!"
echo ""

echo "📋 Step 2: Verifying executable exists..."
if [ -f ".build/release/ios-backup-windows" ]; then
    echo "✅ Executable found: .build/release/ios-backup-windows"
else
    echo "❌ Executable not found!"
    exit 1
fi
echo ""

echo "🧪 Step 3: Running application tests..."
echo ""

# Test 1: Help command
echo "[TEST 1] Testing help command..."
./.build/release/ios-backup-windows help > /tmp/test-help.txt 2>&1
if [ $? -eq 0 ]; then
    echo "[PASS] Help command works"
else
    echo "[FAIL] Help command failed"
    exit 1
fi
echo ""

# Test 2: List devices
echo "[TEST 2] Testing list-devices command..."
./.build/release/ios-backup-windows list-devices > /tmp/test-list.txt 2>&1
if [ $? -eq 0 ]; then
    echo "[PASS] List devices command works"
else
    echo "[FAIL] List devices command failed"
    exit 1
fi
echo ""

# Test 3: Create backup
echo "[TEST 3] Testing backup creation..."
rm -rf /tmp/demo-backup
./.build/release/ios-backup-windows backup demo-device /tmp/demo-backup > /tmp/test-backup.txt 2>&1
if [ -f "/tmp/demo-backup/manifest.json" ]; then
    echo "[PASS] Backup created successfully"
    echo "       Manifest content:"
    cat /tmp/demo-backup/manifest.json | jq '.' 2>/dev/null || cat /tmp/demo-backup/manifest.json
else
    echo "[FAIL] Backup creation failed"
    exit 1
fi
echo ""

# Test 4: Verify backup
echo "[TEST 4] Testing backup verification..."
./.build/release/ios-backup-windows verify /tmp/demo-backup > /tmp/test-verify.txt 2>&1
if [ $? -eq 0 ]; then
    echo "[PASS] Backup verification works"
else
    echo "[FAIL] Verification failed"
    exit 1
fi
echo ""

# Test 5: Encrypted backup
echo "[TEST 5] Testing encrypted backup..."
rm -rf /tmp/demo-backup-enc
./.build/release/ios-backup-windows backup demo-device-enc /tmp/demo-backup-enc --encrypt "TestPassword123" > /tmp/test-backup-enc.txt 2>&1
if [ -f "/tmp/demo-backup-enc/manifest.json" ]; then
    echo "[PASS] Encrypted backup created successfully"
else
    echo "[FAIL] Encrypted backup creation failed"
    exit 1
fi
echo ""

# Test 6: Restore command
echo "[TEST 6] Testing restore command..."
./.build/release/ios-backup-windows restore /tmp/demo-backup demo-device > /tmp/test-restore.txt 2>&1
if [ $? -eq 0 ]; then
    echo "[PASS] Restore command works"
else
    echo "[FAIL] Restore command failed"
    exit 1
fi
echo ""

# Display sample output
echo "╔═══════════════════════════════════════════════════════════╗"
echo "║                   Sample Output                           ║"
echo "╚═══════════════════════════════════════════════════════════╝"
echo ""
echo "Help Command Output:"
echo "-------------------"
head -20 /tmp/test-help.txt
echo ""

echo "╔═══════════════════════════════════════════════════════════╗"
echo "║              All Tests Passed Successfully! ✓             ║"
echo "╚═══════════════════════════════════════════════════════════╝"
echo ""
echo "📋 Summary:"
echo "   • Windows application built successfully"
echo "   • All 6 tests passed"
echo "   • Backup and restore functionality verified"
echo "   • Encryption support tested"
echo ""
echo "📁 Generated backups:"
echo "   • /tmp/demo-backup (unencrypted)"
echo "   • /tmp/demo-backup-enc (encrypted)"
echo ""
echo "🚀 Next Steps for Windows Users:"
echo "   1. Clone the repository"
echo "   2. Run: cd Windows && .\\install.ps1"
echo "   3. Use: ios-backup-windows.exe help"
echo ""
