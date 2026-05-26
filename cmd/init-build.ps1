#!/usr/bin/env pwsh
# init-build.ps1 - Initialize the build environment for Veldrid project
# This script ensures all required prerequisites are installed and configured

param(
    [switch]$Force
)

$ErrorActionPreference = 'Stop'

Write-Host "Initializing Veldrid build environment..." -ForegroundColor Cyan
Write-Host ""

# 1. Check for .NET SDK
Write-Host "Checking for .NET SDK..." -ForegroundColor Yellow
$dotnetOk = $null
try {
    $sdks = dotnet --list-sdks 2>$null
    if ($sdks) {
        Write-Host "  ✓ .NET SDK is installed" -ForegroundColor Green
        $dotnetOk = $true
    }
} catch {
    # dotnet not found or error
}

if (-not $dotnetOk) {
    Write-Host "  × .NET SDK not found" -ForegroundColor Red
    Write-Host "  Please install .NET SDK from: https://dotnet.microsoft.com/download" -ForegroundColor Yellow
    exit 1
}

Write-Host ""

# 2. Create Directory.Build.props for local development
Write-Host "Setting up local development configuration..." -ForegroundColor Yellow
$buildPropsFile = Join-Path $PSScriptRoot ".." "Directory.Build.props"

if (Test-Path $buildPropsFile -PathType Leaf -and -not $Force) {
    Write-Host "  ✓ Directory.Build.props already exists" -ForegroundColor Green
} else {
    $content = @"
<Project>
  <PropertyGroup>
    <!-- Use local project references instead of NuGet packages -->
    <UseLocalVeldrid>true</UseLocalVeldrid>
  </PropertyGroup>
</Project>
"@
    
    try {
        Set-Content -Path $buildPropsFile -Value $content -Encoding UTF8
        Write-Host "  ✓ Created Directory.Build.props for local development" -ForegroundColor Green
    } catch {
        Write-Host "  × Failed to create Directory.Build.props: $_" -ForegroundColor Red
        exit 1
    }
}

Write-Host ""

# 3. Restore NuGet packages
Write-Host "Restoring NuGet packages..." -ForegroundColor Yellow
try {
    dotnet restore "$PSScriptRoot/../src/Veldrid.sln"
    if ($LASTEXITCODE -eq 0) {
        Write-Host "  ✓ NuGet packages restored successfully" -ForegroundColor Green
    } else {
        throw "Package restore failed with exit code $LASTEXITCODE"
    }
} catch {
    Write-Host "  × Failed to restore packages: $_" -ForegroundColor Red
    exit 1
}

Write-Host ""
Write-Host "✓ Build environment initialized successfully!" -ForegroundColor Green
Write-Host ""
Write-Host "You can now build the project using:" -ForegroundColor Cyan
Write-Host "  dotnet build src/Veldrid.sln" -ForegroundColor White
Write-Host "or" -ForegroundColor Cyan
Write-Host "  ./cmd/build.ps1" -ForegroundColor White
Write-Host ""