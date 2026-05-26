#!/usr/bin/env pwsh
# build.ps1 — Build Veldrid and deploy packages to local NuGet feed
#
# Usage:
#   .\cmd\build.ps1              # build + deploy to local feed (Debug)
#   .\cmd\build.ps1 -Release     # build in Release config
#
param(
    [switch]$Release
)

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'

# Resolve project root (one level up from cmd/)
$projectRoot = Split-Path -Parent $PSScriptRoot
$solutionPath = Join-Path $projectRoot 'src\Veldrid.sln'

# Set local NuGet feed path so DeployToLocalNuGet target copies the .nupkg
$env:LOCAL_NUGET_REPO = 'C:\PROJECTS\LocalNuGet'

$buildConfig = if ($Release) { 'Release' } else { 'Debug' }

# Capture timestamp ONCE so all projects in the solution get the same version
$now = [System.DateTime]::Now
$buildYYMM   = $now.ToString('yyMM')
$buildDDHH   = $now.ToString('ddHH')
$buildmmss   = $now.ToString('mmss')
$buildYYMMDD = $now.ToString('yyMMdd')
$buildHHmmss = $now.ToString('HHmmss')

Write-Host "`n=== Building Veldrid ($buildConfig) ===" -ForegroundColor Cyan
Write-Host "Local NuGet feed: $env:LOCAL_NUGET_REPO" -ForegroundColor DarkGray
dotnet build $solutionPath -c $buildConfig /p:UseLocalVeldrid=true `
    /p:_BuildYYMM=$buildYYMM /p:_BuildDDHH=$buildDDHH /p:_Buildmmss=$buildmmss `
    /p:_BuildYYMMDD=$buildYYMMDD /p:_BuildHHmmss=$buildHHmmss
if ($LASTEXITCODE -ne 0) {
    Write-Host "ERROR: dotnet build failed with exit code $LASTEXITCODE" -ForegroundColor Red
    exit $LASTEXITCODE
}

Write-Host "`n=== Done! ===" -ForegroundColor Green