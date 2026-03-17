#!/usr/bin/env pwsh
# build.ps1 — Build prerelease packages and deploy to local NuGet feed
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

Write-Host "`n=== Building prerelease packages ($buildConfig) ===" -ForegroundColor Cyan
Write-Host "Local NuGet feed: $env:LOCAL_NUGET_REPO" -ForegroundColor DarkGray
dotnet build $solutionPath -c $buildConfig /p:UseLocalVeldrid=true
if ($LASTEXITCODE -ne 0) {
    Write-Host "ERROR: dotnet build failed with exit code $LASTEXITCODE" -ForegroundColor Red
    exit $LASTEXITCODE
}

Write-Host "`n=== Done! ===" -ForegroundColor Green