#!/usr/bin/env pwsh
# publish-local.ps1 — Build and pack Veldrid packages to local NuGet feed
#
# Versioning is timestamp-based (v2) — every build gets a unique version
# automatically via AN.Veldrid.Build.props. No version files to manage.
# The timestamp is captured once here and passed to MSBuild so all projects
# in the solution get the exact same version (no inter-project skew).
#
# Usage:
#   ./cmd/publish-local.ps1                    # Debug build + pack + deploy
#   ./cmd/publish-local.ps1 -Release           # Release configuration
#
# Requires: LOCAL_NUGET_REPO environment variable set to local feed path

param(
    [switch]$Release
)

$ErrorActionPreference = "Stop"
$repoRoot = Split-Path -Parent (Split-Path -Parent $PSCommandPath)
$solutionPath = Join-Path $repoRoot 'Veldrid.sln'
$configuration = if ($Release) { "Release" } else { "Debug" }

Write-Host "=== Veldrid publish-local ($configuration) ===" -ForegroundColor Cyan

if (-not $env:LOCAL_NUGET_REPO) {
    Write-Host "ERROR: LOCAL_NUGET_REPO environment variable not set." -ForegroundColor Red
    Write-Host '$env:LOCAL_NUGET_REPO = "C:\PROJECTS\LocalNuGet"' -ForegroundColor Yellow
    exit 1
}

Write-Host "Local NuGet feed: $env:LOCAL_NUGET_REPO" -ForegroundColor Gray

# Capture timestamp ONCE so all projects in the solution get the same version
$now = [System.DateTime]::Now
$buildYYMM   = $now.ToString('yyMM')
$buildDDHH   = $now.ToString('ddHH')
$buildmmss   = $now.ToString('mmss')
$buildYYMMDD = $now.ToString('yyMMdd')
$buildHHmmss = $now.ToString('HHmmss')
$versionProps = "/p:_BuildYYMM=$buildYYMM", "/p:_BuildDDHH=$buildDDHH", "/p:_Buildmmss=$buildmmss", "/p:_BuildYYMMDD=$buildYYMMDD", "/p:_BuildHHmmss=$buildHHmmss"

Write-Host "Version stamp: 5.$buildYYMM.$buildDDHH.$buildmmss (pkg: 5.$buildYYMMDD.$buildHHmmss)" -ForegroundColor Gray

# Capture timestamp before build/pack so we can identify newly deployed packages
$deployStartTime = Get-Date

# Build the solution with local project references
Write-Host "`n[1/2] Building solution..." -ForegroundColor Green
dotnet build $solutionPath -c $configuration /p:UseLocalVeldrid=true @versionProps
if ($LASTEXITCODE -ne 0) { exit $LASTEXITCODE }

# Pack all packable projects
Write-Host "`n[2/2] Packing..." -ForegroundColor Green
dotnet pack $solutionPath -c $configuration /p:UseLocalVeldrid=true --no-build @versionProps
if ($LASTEXITCODE -ne 0) { exit $LASTEXITCODE }

# Show only packages deployed during this run (modified after $deployStartTime)
$deployedPackages = Get-ChildItem "$env:LOCAL_NUGET_REPO\*.nupkg" -ErrorAction SilentlyContinue |
    Where-Object { $_.LastWriteTime -ge $deployStartTime } |
    Sort-Object Name
if ($deployedPackages) {
    Write-Host "`nDeployed packages:" -ForegroundColor Cyan
    foreach ($deployedPackage in $deployedPackages) {
        $sizeKB = [math]::Round($deployedPackage.Length / 1024, 1)
        Write-Host "  $($deployedPackage.Name)  (${sizeKB} KB)" -ForegroundColor Green
    }
} else {
    Write-Host "`nWARNING: No packages were deployed to $env:LOCAL_NUGET_REPO" -ForegroundColor Yellow
}