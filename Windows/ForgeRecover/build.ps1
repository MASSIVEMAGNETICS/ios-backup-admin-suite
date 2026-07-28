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
$testProject = Join-Path $root 'tests\ForgeRecover.Core.Tests\ForgeRecover.Core.Tests.csproj'
$output = Join-Path $root "artifacts\forge-recover-$Runtime"

Write-Host 'Restoring projects...'
dotnet restore $cliProject
dotnet restore $testProject

Write-Host 'Building CLI...'
dotnet build $cliProject --configuration $Configuration --no-restore

Write-Host 'Running tests...'
dotnet test $testProject --configuration $Configuration --no-restore --collect 'XPlat Code Coverage'

if (Test-Path $output) {
    Remove-Item $output -Recurse -Force
}

$publishArguments = @(
    'publish',
    $cliProject,
    '--configuration', $Configuration,
    '--runtime', $Runtime,
    '--no-restore',
    '--output', $output,
    '--property:PublishSingleFile=true',
    "--self-contained=$($SelfContained.IsPresent.ToString().ToLowerInvariant())"
)

Write-Host "Publishing $Runtime executable..."
& dotnet @publishArguments

Write-Host ''
Write-Host "ForgeRecover build completed: $output" -ForegroundColor Green
