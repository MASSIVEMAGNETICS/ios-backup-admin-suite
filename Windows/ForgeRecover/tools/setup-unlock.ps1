[CmdletBinding()]
param([string]$Python = 'python')
$ErrorActionPreference = 'Stop'
$root = Split-Path -Parent $MyInvocation.MyCommand.Path
$venv = Join-Path $root '.venv'
& $Python -m venv $venv
$pythonExe = Join-Path $venv 'Scripts\python.exe'
& $pythonExe -m pip install --upgrade pip
& $pythonExe -m pip install -r (Join-Path $root 'requirements-unlock.txt')
& $pythonExe (Join-Path $root 'forge_ios_backup_unlock.py') --self-test
Write-Host "Encrypted-backup helper ready: $pythonExe" -ForegroundColor Green
