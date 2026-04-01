@echo off
REM clean.bat — Remove all build artifacts and run dotnet clean
REM Usage: cmd\clean.bat  (from repo root)

setlocal

REM Validate we're running from the project root
if not exist "AN.Veldrid.Build.props" (
    echo ERROR: AN.Veldrid.Build.props not found in current directory.
    echo This script must be run from the AN_Veldrid project root.
    echo Example: cmd\clean.bat
    exit /b 1
)

REM Resolve repo root (one level up from cmd\)
set "REPO_ROOT=%~dp0.."

echo === Cleaning AN_Veldrid build artifacts ===

echo Removing bin\ ...
if exist "%REPO_ROOT%\bin" rmdir /s /q "%REPO_ROOT%\bin"

echo Removing obj\ directories under src\ ...
for /d /r "%REPO_ROOT%\src" %%d in (obj) do (
    if exist "%%d" (
        echo   %%d
        rmdir /s /q "%%d"
    )
)

echo Running dotnet clean ...
dotnet clean "%REPO_ROOT%\src\Veldrid.sln" -c Debug --nologo -v q
dotnet clean "%REPO_ROOT%\src\Veldrid.sln" -c Release --nologo -v q

echo === Clean complete ===