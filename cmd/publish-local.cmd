@echo off
REM publish-local.cmd - Thin wrapper that launches the cross-platform C# publish script from the repo root.
REM Usage: cmd\publish-local.cmd [--dry-run]
REM Always publishes a RELEASE build (see publish-local.cs for why Debug is refused).
pushd "%~dp0.."
dotnet run --file "%~dp0publish-local.cs" -- %*
set EXITCODE=%ERRORLEVEL%
popd
exit /b %EXITCODE%