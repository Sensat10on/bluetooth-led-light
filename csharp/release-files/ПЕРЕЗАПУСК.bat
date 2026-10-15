@echo off
chcp 65001 >nul
cd /d "%~dp0"

rem ============================================================================
rem  OpenTuya Sync -- restart the application window.
rem
rem  Closes running copies and starts one new one. The service is NOT touched:
rem  stopping it needs administrator rights, and there is a separate file for
rem  that -- ПЕРЕЗАПУСК-СЛУЖБЫ.bat.
rem
rem  Needed because a running copy keeps the old code in memory: after
rem  updating the program it has to be restarted.
rem
rem  Text here is ASCII only -- see the note in НАСТРОИТЬ.bat.
rem ============================================================================

echo Closing running copies of OpenTuya Sync...
taskkill /IM OpenTuyaSync.exe /F >nul 2>&1
timeout /t 3 /nobreak >nul

echo Starting the application...
start "" "%~dp0OpenTuyaSync.exe"

echo.
echo Done. In a few seconds check the status line in the window: it should
echo say the service is connected.
echo.
echo If it says the service is not running, start the service restart script
echo (the file named PEREZAPUSK-SLUZHBY) -- it will ask for administrator
echo rights.
echo.
echo The reason for a connection failure is written to:
echo   %~dp0service_link.log
timeout /t 8 /nobreak >nul
