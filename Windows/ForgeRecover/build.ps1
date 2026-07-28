[CmdletBinding()]
param(
    [ValidateSet('Debug', 'Release')]
    [string]$Configuration = 'Release',

    [string]$Runtime = 'win-x64',

    [switch]$SelfContained
)

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'

$root = Split-Path -Parent $MyInvocation.MyCommand.Path
$cliProject = Join-Path $root 'src\ForgeRecover.Cli\ForgeRecover.Cli.csproj'
$appProject = Join-Path $root 'src\ForgeRecover.App\ForgeRecover.App.csproj'
$testProject = Join-Path $root 'tests\ForgeRecover.Core.Tests\ForgeRecover.Core.Tests.csproj'
$platformName = if ($Runtime.StartsWith('win-', [StringComparison]::OrdinalIgnoreCase)) {
    'windows-' + $Runtime.Substring(4)
} else {
    $Runtime
}
$suiteOutput = Join-Path $root "artifacts\forge-recover-$platformName"
$cliOutput = Join-Path $suiteOutput 'cli'
$appOutput = Join-Path $suiteOutput 'workbench'
$selfContainedValue = $SelfContained.IsPresent.ToString().ToLowerInvariant()

Write-Host 'Restoring Windows products...'
dotnet restore $cliProject --runtime $Runtime
dotnet restore $appProject --runtime $Runtime
dotnet restore $testProject

Write-Host 'Building CLI...'
dotnet build $cliProject --configuration $Configuration --runtime $Runtime --no-restore

Write-Host 'Building WPF workbench...'
dotnet build $appProject --configuration $Configuration --runtime $Runtime --no-restore

Write-Host 'Running deterministic tests...'
dotnet test $testProject --configuration $Configuration --no-restore --collect 'XPlat Code Coverage'

if (Test-Path $suiteOutput) {
    Remove-Item $suiteOutput -Recurse -Force
}
New-Item -ItemType Directory -Force $cliOutput, $appOutput | Out-Null

Write-Host "Publishing CLI for $Runtime..."
dotnet publish $cliProject `
    --configuration $Configuration `
    --runtime $Runtime `
    --self-contained $selfContainedValue `
    --no-restore `
    --output $cliOutput

Write-Host "Publishing WPF workbench for $Runtime..."
dotnet publish $appProject `
    --configuration $Configuration `
    --runtime $Runtime `
    --self-contained $selfContainedValue `
    --no-restore `
    --output $appOutput

Copy-Item (Join-Path $root 'README.md') (Join-Path $suiteOutput 'README.md')
$workbenchGuide = Join-Path $root 'WORKBENCH.md'
if (Test-Path $workbenchGuide) {
    Copy-Item $workbenchGuide (Join-Path $suiteOutput 'WORKBENCH.md')
}

Write-Host 'Running published CLI smoke test...'
& (Join-Path $cliOutput 'forge-recover.exe') help | Set-Content (Join-Path $suiteOutput 'cli-smoke-test.txt')
if ($LASTEXITCODE -ne 0) {
    throw 'Published CLI smoke test failed.'
}

$workbenchExecutable = Join-Path $appOutput 'ForgeRecover.Workbench.exe'
if (-not (Test-Path $workbenchExecutable)) {
    throw "Published workbench executable was not found: $workbenchExecutable"
}

Write-Host 'Generating release SHA-256 manifest...'
$hashLines = Get-ChildItem $suiteOutput -File -Recurse |
    Sort-Object FullName |
    ForEach-Object {
        $relative = [IO.Path]::GetRelativePath((Resolve-Path $suiteOutput), $_.FullName).Replace('\', '/')
        $hash = (Get-FileHash $_.FullName -Algorithm SHA256).Hash.ToLowerInvariant()
        "$hash  $relative"
    }
$hashLines | Set-Content (Join-Path $suiteOutput 'SHA256SUMS.txt') -Encoding utf8

Write-Host ''
Write-Host "ForgeRecover Windows suite completed: $suiteOutput" -ForegroundColor Green
Write-Host "Workbench: $workbenchExecutable" -ForegroundColor Green
Write-Host "CLI:       $(Join-Path $cliOutput 'forge-recover.exe')" -ForegroundColor Green
