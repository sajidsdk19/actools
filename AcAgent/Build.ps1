<#
.SYNOPSIS
    Builds AcTools.dll (the dependency) then builds AcAgent.

.DESCRIPTION
    Run this once from the repository root to produce a working AcAgent binary.
    Requires: msbuild (Visual Studio 2022 / Build Tools), nuget.exe, and .NET 6 SDK.

.PARAMETER Configuration
    Debug (default) or Release.

.PARAMETER AcRoot
    Path to your Assetto Corsa installation.
    Defaults to the standard Steam path.

.EXAMPLE
    # From repo root:
    .\AcAgent\Build.ps1

    # Release build with a custom AC path:
    .\AcAgent\Build.ps1 -Configuration Release -AcRoot "D:\Steam\steamapps\common\assettocorsa"
#>
param(
    [ValidateSet("Debug","Release")]
    [string]$Configuration = "Debug",

    [string]$AcRoot = "C:\Program Files (x86)\Steam\steamapps\common\assettocorsa"
)

Set-StrictMode -Version Latest
$ErrorActionPreference = "Stop"

$RepoRoot  = Resolve-Path "$PSScriptRoot\.."
$AgentRoot = "$RepoRoot\AcAgent"

Write-Host "==============================================================" -ForegroundColor Cyan
Write-Host "  AcAgent Build Script" -ForegroundColor Cyan
Write-Host "  Configuration : $Configuration" -ForegroundColor Cyan
Write-Host "  AC Root       : $AcRoot" -ForegroundColor Cyan
Write-Host "==============================================================" -ForegroundColor Cyan

# ── Step 1: Locate tools ─────────────────────────────────────────────────────

function Find-Tool([string]$name) {
    $path = Get-Command $name -ErrorAction SilentlyContinue | Select-Object -ExpandProperty Source
    if ($null -eq $path) {
        Write-Error "Could not find '$name' on PATH. Please install it and try again."
    }
    return $path
}

$msbuild = Find-Tool "msbuild"
$dotnet  = Find-Tool "dotnet"

# nuget.exe is optional — skip restore if not found
$nuget = Get-Command "nuget" -ErrorAction SilentlyContinue | Select-Object -ExpandProperty Source

Write-Host ""
Write-Host "[Step 1] Tools located" -ForegroundColor Green
Write-Host "  msbuild : $msbuild"
Write-Host "  dotnet  : $dotnet"
if ($nuget) { Write-Host "  nuget   : $nuget" }
else         { Write-Warning "  nuget.exe not found — skipping package restore for AcTools.csproj" }

# ── Step 2: Restore NuGet packages for AcTools ───────────────────────────────

Write-Host ""
Write-Host "[Step 2] Restoring NuGet packages for AcTools..." -ForegroundColor Green
if ($nuget) {
    Push-Location $RepoRoot
    & $nuget restore AcManager.sln
    if ($LASTEXITCODE -ne 0) { Write-Error "NuGet restore failed." }
    Pop-Location
} else {
    Write-Warning "Skipped — install nuget.exe from https://www.nuget.org/downloads if you see MSBuild package errors."
}

# ── Step 3: Build AcTools.dll ────────────────────────────────────────────────

Write-Host ""
Write-Host "[Step 3] Building AcTools.dll ($Configuration|x86)..." -ForegroundColor Green

$acToolsProject = "$RepoRoot\AcTools\AcTools.csproj"
& $msbuild $acToolsProject `
    /p:Configuration=$Configuration `
    /p:Platform=x86 `
    /verbosity:minimal `
    /nologo

if ($LASTEXITCODE -ne 0) { Write-Error "AcTools.dll build failed." }

$acToolsDll = "$RepoRoot\Output\x86\$Configuration\AcTools.dll"
if (-not (Test-Path $acToolsDll)) {
    Write-Error "AcTools.dll not found at expected path: $acToolsDll"
}
Write-Host "  AcTools.dll built: $acToolsDll" -ForegroundColor Green

# ── Step 4: Restore + Build AcAgent ─────────────────────────────────────────

Write-Host ""
Write-Host "[Step 4] Building AcAgent (.NET 6, x86)..." -ForegroundColor Green

Push-Location $AgentRoot
& $dotnet restore AcAgent.csproj
if ($LASTEXITCODE -ne 0) { Write-Error "dotnet restore failed." }

& $dotnet build AcAgent.csproj -c $Configuration --no-restore
if ($LASTEXITCODE -ne 0) { Write-Error "AcAgent build failed." }
Pop-Location

# ── Step 5: Quick smoke-test ─────────────────────────────────────────────────

Write-Host ""
Write-Host "[Step 5] Smoke-test: listing available cars..." -ForegroundColor Green

$agentExe = "$AgentRoot\bin\x86\$Configuration\net6.0-windows\AcAgent.exe"
if (Test-Path $agentExe) {
    & $agentExe --ac-root $AcRoot --list-cars
} else {
    Write-Warning "AcAgent.exe not found at $agentExe — skipping smoke test."
    Write-Host "  You can also run: dotnet run --project $AgentRoot -- --ac-root `"$AcRoot`" --list-cars"
}

Write-Host ""
Write-Host "==============================================================" -ForegroundColor Green
Write-Host "  Build complete!" -ForegroundColor Green
Write-Host ""
Write-Host "  To launch a session (30-minute Practice):" -ForegroundColor Yellow
Write-Host "    $agentExe --ac-root `"$AcRoot`" --car lotus_elise_sc --track magione --duration 30" -ForegroundColor Yellow
Write-Host ""
Write-Host "  Or use 'dotnet run' from the AcAgent folder:" -ForegroundColor Yellow
Write-Host "    cd $AgentRoot" -ForegroundColor Yellow
Write-Host "    dotnet run -- --ac-root `"$AcRoot`" --car lotus_elise_sc --track magione --duration 30" -ForegroundColor Yellow
Write-Host "==============================================================" -ForegroundColor Green
