@echo off
rem ============================================================================
rem  Build the release from scratch.
rem
rem    Releases\                    application and service with all libraries
rem    Releases\tools\ColorTest\    ambilight color test tool
rem
rem  Usage:  publish-release.cmd [--quiet]
rem          --quiet  no pause at the end (for automatic builds)
rem ============================================================================
chcp 65001 >nul
cd /d "%~dp0"

set QUIET=0
if /i "%~1"=="--quiet" set QUIET=1

set RELEASE=Releases
set TOOLDIR=%RELEASE%\tools\ColorTest
set FILES=release-files

echo Stopping running copies...
taskkill /IM OpenTuyaSync.exe /F >nul 2>&1
taskkill /IM OpenTuyaSync.ColorTest.exe /F >nul 2>&1
sc stop OpenTuyaSync >nul 2>&1
timeout /t 3 /nobreak >nul

echo Cleaning release folder...
if exist "%RELEASE%" rmdir /s /q "%RELEASE%"
mkdir "%RELEASE%"
mkdir "%TOOLDIR%"

echo.
echo Publishing application...
dotnet publish OpenTuyaSync.Agent\OpenTuyaSync.Agent.csproj -c Release -o "%RELEASE%" --nologo -v q
if errorlevel 1 goto fail

echo Publishing service...
dotnet publish OpenTuyaSync.Service\OpenTuyaSync.Service.csproj -c Release -o "%RELEASE%" --nologo -v q
if errorlevel 1 goto fail

echo Publishing color test tool...
dotnet publish tools\ColorTest\ColorTest.csproj -c Release -o "%TOOLDIR%" --nologo -v q
if errorlevel 1 goto fail

echo.
echo Preparing extra files...

rem Debug leftovers are not part of the release.
del /q "%RELEASE%\agent_alive.txt" 2>nul
del /q "%RELEASE%\service_link.log" 2>nul
del /q "%TOOLDIR%\selftest_*.png" 2>nul
del /q "%TOOLDIR%\selftest_report.txt" 2>nul
del /q "%TOOLDIR%\colortest_crash.log" 2>nul
del /q "%TOOLDIR%\colortest_alive.txt" 2>nul

rem Debug symbols only make the release bigger.
del /q "%RELEASE%\*.pdb" 2>nul
del /q "%TOOLDIR%\*.pdb" 2>nul

rem Extra files: settings, instructions, icons. Cyrillic names are taken from
rem the source folder as-is, so no non-ASCII text is needed in this file.
for %%F in ("%FILES%\config.json" "%FILES%\*.bat" "%FILES%\*.txt" "assets\icon.ico") do (
  copy /y "%%~F" "%RELEASE%\" >nul
)

rem Full user guide. The source is markdown (convenient to read on GitHub);
rem for the release the markup is stripped, so it opens as plain text.
powershell -NoProfile -ExecutionPolicy Bypass -File make-guide.ps1 -Output "%RELEASE%"
if errorlevel 1 goto fail

if errorlevel 1 goto fail

echo.
echo Done.
if "%QUIET%"=="1" exit /b 0
pause
exit /b 0

:fail
echo.
echo Build failed. See the output above.
if "%QUIET%"=="1" exit /b 1
pause
exit /b 1
