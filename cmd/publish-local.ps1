#!/usr/bin/env pwsh
# publish-local.ps1 — Build release packages and deploy to local NuGet feed
#
# Increments buildNumberOffset in version.json using the JsonPeek CLI tool
# from the ArtificialNecessity.CodeAnalyzers package, then builds all
# packable projects with clean (non-prerelease) version numbers.
#
# Usage:
#   .\cmd\publish-local.ps1              # increment + build + deploy
#   .\cmd\publish-local.ps1 -DryRun      # show what would happen, don't build
#
param(
    [switch]$DryRun
)

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'

# Resolve project root (one level up from cmd/)
$projectRoot = Split-Path -Parent $PSScriptRoot
$solutionPath = Join-Path $projectRoot 'src\Veldrid.sln'
$versionJsonPath = Join-Path $projectRoot 'version.json'
$buildPropsPath = Join-Path $projectRoot 'AN.Veldrid.Build.props'
$localNuGetFeedPath = 'C:\PROJECTS\LocalNuGet'

# ── Resolve JsonPeek CLI tool from NuGet cache ──────────────────────────
# Read the AN.CodeAnalyzers version from AN.Veldrid.Build.props
$buildPropsContent = Get-Content $buildPropsPath -Raw
$codeAnalyzersVersionMatch = [regex]::Match($buildPropsContent, 'ArtificialNecessity\.CodeAnalyzers.*?Version="([^"]+)"')
if (-not $codeAnalyzersVersionMatch.Success) {
    Write-Host "ERROR: Could not find ArtificialNecessity.CodeAnalyzers version in $buildPropsPath" -ForegroundColor Red
    exit 1
}
$codeAnalyzersVersion = $codeAnalyzersVersionMatch.Groups[1].Value
$jsonPeekExePath = Join-Path $env:USERPROFILE ".nuget\packages\artificialnecessity.codeanalyzers\$codeAnalyzersVersion\tools\net8.0\any\JsonPeek.exe"

if (-not (Test-Path $jsonPeekExePath)) {
    Write-Host "ERROR: JsonPeek CLI tool not found at: $jsonPeekExePath" -ForegroundColor Red
    Write-Host "Run 'dotnet restore src\Veldrid.sln' to download the package." -ForegroundColor Yellow
    exit 1
}

# ── Read current version info ────────────────────────────────────────────
$baseVersion = & $jsonPeekExePath $versionJsonPath version
$currentBuildNumberOffset = & $jsonPeekExePath $versionJsonPath buildNumberOffset
$currentVersion = "$baseVersion.$currentBuildNumberOffset"

Write-Host "`n=== Publishing release to local NuGet feed ===" -ForegroundColor Cyan
Write-Host "Current version: $currentVersion" -ForegroundColor DarkGray
Write-Host "JsonPeek tool:   $jsonPeekExePath" -ForegroundColor DarkGray

# ── Increment buildNumberOffset in version.json ─────────────────────────
$newBuildNumberOffset = & $jsonPeekExePath --inc-integer $versionJsonPath buildNumberOffset
$newVersion = "$baseVersion.$newBuildNumberOffset"

Write-Host "New version:     $newVersion" -ForegroundColor Green
Write-Host "Local feed:      $localNuGetFeedPath" -ForegroundColor DarkGray

if ($DryRun) {
    Write-Host "`n[DRY RUN] Would build release version $newVersion and deploy to $localNuGetFeedPath" -ForegroundColor Yellow
    # Revert the increment since this is a dry run
    & $jsonPeekExePath --inc-integer $versionJsonPath buildNumberOffset -1 | Out-Null
    Write-Host "[DRY RUN] Reverted buildNumberOffset back to $currentBuildNumberOffset" -ForegroundColor Yellow
    exit 0
}

# ── Build release packages ──────────────────────────────────────────────
$env:LOCAL_NUGET_REPO = $localNuGetFeedPath

Write-Host "`n=== Building release packages ===" -ForegroundColor Cyan
dotnet build $solutionPath -c Release /p:NewRelease=true /p:UseLocalVeldrid=true
if ($LASTEXITCODE -ne 0) {
    Write-Host "ERROR: dotnet build failed with exit code $LASTEXITCODE" -ForegroundColor Red
    exit $LASTEXITCODE
}

Write-Host "`n=== Done! ===" -ForegroundColor Green
Write-Host "Published release version: $newVersion" -ForegroundColor Green
Write-Host "Packages deployed to: $localNuGetFeedPath" -ForegroundColor Green