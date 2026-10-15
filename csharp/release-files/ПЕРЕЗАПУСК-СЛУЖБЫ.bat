@echo off
chcp 65001 >nul
cd /d "%~dp0"

rem ============================================================================
rem  OpenTuya Sync -- restart the Windows service.
rem
rem  Separate file because stopping the service needs administrator rights.
rem  The ordinary ПЕРЕЗАПУСК.bat asks for nothing and only restarts the
rem  application window.
rem
rem  Text here is ASCII only -- see the note in НАСТРОИТЬ.bat.
rem ============================================================================

setlocal
set SERVICE_EXE=%~dp0OpenTuyaSync.Service.exe

echo ============================================================
echo   Restarting the OpenTuyaSync service
echo ============================================================
echo.

if not exist "%SERVICE_EXE%" (
  echo ERROR: not found: %SERVICE_EXE%
  echo Run this file from the program folder.
  pause
  exit /b 1
)

rem Check rights: without them the service cannot be stopped.
net session >nul 2>&1
if errorlevel 1 (
  echo Administrator rights are required.
  echo A prompt will appear now -- press "Yes".
  echo.
  powershell -NoProfile -Command "Start-Process -FilePath '%~f0' -Verb RunAs"
  exit /b 0
)

echo Step 1 of 3. Stopping the service.
sc stop OpenTuyaSync >nul 2>&1
timeout /t 8 /nobreak >nul

echo Step 2 of 3. Starting the service.
sc start OpenTuyaSync >nul 2>&1
timeout /t 10 /nobreak >nul

echo Step 3 of 3. State:
sc query OpenTuyaSync | findstr /C:"STATE"

echo.
echo Done. Check the connection in the application: the status line
echo should say the service is connected.
echo.
echo If the service did not start, look at the Windows event log:
echo   Event Viewer -^> Windows Logs -^> Application, source OpenTuyaSync
echo.
pause
