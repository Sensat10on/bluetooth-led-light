@echo off
rem ============================================================================
rem  Build the release: the Releases folder AND the archive next to it.
rem
rem  The actual work is done by publish-release.ps1: it builds the projects in
rem  parallel (sequential builds of eight projects take minutes) and packs the
rem  archive.
rem
rem  Usage:  publish-release.cmd [--quiet]
rem          --quiet  no pause at the end (for automatic builds)
rem ============================================================================
chcp 65001 >nul
cd /d "%~dp0"

set QUIET=0
if /i "%~1"=="--quiet" set QUIET=1

powershell -NoProfile -ExecutionPolicy Bypass -File "%~dp0publish-release.ps1"
set RESULT=%ERRORLEVEL%

if not "%RESULT%"=="0" (
  echo.
  echo Build failed. See the output above.
  if "%QUIET%"=="1" exit /b %RESULT%
  pause
  exit /b %RESULT%
)

if "%QUIET%"=="1" exit /b 0
pause
exit /b 0
