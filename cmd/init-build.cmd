@echo off
REM init-build.cmd - Initialize the build environment for Veldrid project
REM This is a wrapper around the PowerShell script for easier execution

PowerShell -ExecutionPolicy Bypass -File "%~dp0init-build.ps1" %*

if %ERRORLEVEL% neq 0 (
    echo.
    echo Build initialization failed!
    exit /b %ERRORLEVEL%
)