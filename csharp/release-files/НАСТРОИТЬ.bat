@echo off
chcp 65001 >nul
cd /d "%~dp0"

rem ============================================================================
rem  OpenTuya Sync -- full setup in one double click.
rem
rem  Steps:
rem    1. removes the "downloaded from the internet" mark from all files
rem    2. registers the service (needs administrator confirmation)
rem    3. registers the application to start at logon, minimised to tray,
rem       and starts it
rem    4. checks that the service answered
rem
rem  IMPORTANT ABOUT STARTUP. The service is installed with the "manual"
rem  start type: it does NOT start with Windows any more. Startup is done by
rem  the application, which appears at logon minimised to the notification
rem  area and starts the service itself. The reason is simple: the service has
rem  no interface, so with a service-only startup the user could never see
rem  whether anything works.
rem
rem  IMPORTANT: the text inside this file is ASCII only.
rem  cmd.exe reads a batch file using the console code page. This machine uses
rem  code page 866, and Russian text written in UTF-8 breaks command parsing:
rem  comment lines get executed as commands. That is why all launcher scripts
rem  are ASCII, while the Russian documentation lives in CHITAT.txt and
rem  SPRAVKA.txt.
rem ============================================================================

setlocal
set DIST=%~dp0
set SERVICE=%DIST%OpenTuyaSync.Service.exe
set AGENT=%DIST%OpenTuyaSync.exe

echo ============================================================
echo   OpenTuya Sync setup
echo ============================================================
echo.

if not exist "%SERVICE%" (
  echo ERROR: not found: %SERVICE%
  echo Unpack the whole archive and run this file from the program folder.
  pause
  exit /b 1
)

if not exist "%AGENT%" (
  echo ERROR: not found: %AGENT%
  pause
  exit /b 1
)

echo Step 1 of 4. Removing the "downloaded from the internet" mark.
echo   Windows marks downloaded files and then warns about each program.
echo   The mark is removed here once for all files.
powershell -NoProfile -ExecutionPolicy Bypass -Command "$root = '%~dp0'; $files = @(Get-ChildItem -LiteralPath $root -Recurse -File -ErrorAction SilentlyContinue); foreach ($f in $files) { Unblock-File -LiteralPath $f.FullName -ErrorAction SilentlyContinue }; Write-Host ('  files processed: ' + $files.Count)"
echo.

echo Step 2 of 4. Registering the service.
echo   The service will be set to start manually: the application starts it.
echo   An administrator prompt will appear -- press "Yes".
echo.
"%SERVICE%" install
if errorlevel 1 (
  echo.
  echo Could not register the service.
  echo Run this file as administrator and try again.
  pause
  exit /b 1
)

echo.
echo Step 3 of 4. Registering the application to start at logon.
echo   The application will appear minimised in the notification area and
echo   start the service itself. The setting is stored in the application,
echo   not in the registry by this script.
start "" "%AGENT%" --enable-autostart --show
timeout /t 10 /nobreak >nul

echo Step 4 of 4. Checking the service.
sc query OpenTuyaSync | findstr /C:"STATE"
echo.

echo ============================================================
echo   Done.
echo ============================================================
echo.
echo The application is now running in the notification area and should
echo have started the service. In the window the status line must say the
echo service is connected; then open the "Automatic" tab and press "Start".
echo.
echo If the icon is not visible in the notification area, Windows 11 has
echo hidden it: press the "^" arrow (show hidden icons) or run this setup
echo again -- the running window will open by itself.
echo.
echo To disable startup with Windows: open the application, Settings,
echo uncheck "Start with Windows".
echo.
echo Russian instructions are in the files CHITAT.txt and SPRAVKA.txt
echo inside this folder.
echo.
pause
