@echo off
chcp 65001 >nul
cd /d "%~dp0"

rem ============================================================================
rem  OpenTuya Sync -- full setup in one double click.
rem
rem  Steps:
rem    1. removes the "downloaded from the internet" mark from all files
rem    2. registers the service (needs administrator confirmation)
rem    3. starts it
rem    4. checks the state
rem    5. starts the application
rem
rem  IMPORTANT: the text inside this file is ASCII only.
rem  cmd.exe reads a batch file using the console code page. This machine uses
rem  code page 866, and Russian text written in UTF-8 breaks command parsing:
rem  comment lines get executed as commands. That is why all launcher scripts
rem  are ASCII, while the Russian documentation lives in ЧИТАТЬ.txt and
rem  СПРАВКА.txt.
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

echo Step 1 of 5. Removing the "downloaded from the internet" mark.
echo   Windows marks downloaded files and then warns about each program.
echo   The mark is removed here once for all files.
powershell -NoProfile -ExecutionPolicy Bypass -Command "$root = '%~dp0'; $files = @(Get-ChildItem -LiteralPath $root -Recurse -File -ErrorAction SilentlyContinue); foreach ($f in $files) { Unblock-File -LiteralPath $f.FullName -ErrorAction SilentlyContinue }; Write-Host ('  files processed: ' + $files.Count)"
echo.

echo Step 2 of 5. Registering the service.
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
echo Step 3 of 5. Starting the service.
sc start OpenTuyaSync >nul 2>&1
timeout /t 12 /nobreak >nul

echo Step 4 of 5. Checking the state.
sc query OpenTuyaSync | findstr /C:"STATE"
echo.

echo Step 5 of 5. Starting the application.
start "" "%AGENT%"

echo.
echo ============================================================
echo   Done.
echo ============================================================
echo.
echo In the application window the status line should say the service
echo is connected. Then open the "Automatic" tab and press "Start".
echo.
echo If the service does not answer, run the service restart script
echo (the file named PEREZAPUSK-SLUZHBY) or send the log file:
echo   %DIST%service_link.log
echo.
echo Russian instructions are in the files CHITAT.txt and SPRAVKA.txt
echo inside this folder.
echo.
pause
