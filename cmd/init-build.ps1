# init-build.ps1 - Initialize the build environment for Veldrid project
# This script ensures all required prerequisites are installed and configured

param(
    [switch]$Force
)

$ErrorActionPreference = 'Stop'

Write-Host "Initializing Veldrid build environment..." -ForegroundColor Cyan
Write-Host ""

# 1. Check for .NET 8 SDK (required for build tools)
Write-Host "Checking for .NET 8 SDK..." -ForegroundColor Yellow
$dotnet8 = $null
try {
    $sdks = dotnet --list-sdks 2>$null | Select-String "8\.\d+\.\d+"
    if ($sdks) {
        Write-Host "  ✓ .NET 8 SDK is installed" -ForegroundColor Green
        $dotnet8 = $true
    }
} catch {
    # dotnet not found or error
}

if (-not $dotnet8) {
    Write-Host "  × .NET 8 SDK not found" -ForegroundColor Red
    
    if ($IsWindows -or $PSVersionTable.PSVersion.Major -ge 6) {
        Write-Host "  Installing .NET 8 SDK via winget..." -ForegroundColor Yellow
        try {
            winget install --exact --id Microsoft.DotNet.SDK.8 --accept-package-agreements --accept-source-agreements
            if ($LASTEXITCODE -eq 0) {
                Write-Host "  ✓ .NET 8 SDK installed successfully" -ForegroundColor Green
            } else {
                throw "Failed to install .NET 8 SDK"
            }
        } catch {
            Write-Host "  Failed to install .NET 8 SDK automatically" -ForegroundColor Red
            Write-Host "  Please install .NET 8 SDK manually from: https://dotnet.microsoft.com/download/dotnet/8.0" -ForegroundColor Yellow
            exit 1
        }
    } else {
        Write-Host "  Please install .NET 8 SDK from: https://dotnet.microsoft.com/download/dotnet/8.0" -ForegroundColor Yellow
        exit 1
    }
}

Write-Host ""

# 2. Generate version file
Write-Host "Generating version file..." -ForegroundColor Yellow
$versionFile = Join-Path $PSScriptRoot ".." "AN.Veldrid.Version.generated.props"

if (Test-Path $versionFile -PathType Leaf -and -not $Force) {
    Write-Host "  ✓ Version file already exists" -ForegroundColor Green
} else {
    try {
        & "$PSScriptRoot/gen-version-file.ps1"
        Write-Host "  ✓ Version file generated successfully" -ForegroundColor Green
    } catch {
        Write-Host "  × Failed to generate version file: $_" -ForegroundColor Red
        exit 1
    }
}

Write-Host ""

# 3. Create Directory.Build.props for local development
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

# 4. Restore NuGet packages
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