@echo off
chcp 65001 >nul
cd /d "%~dp0"

rem Закрывает все запущенные копии приложения и запускает одну новую.
rem Нужно, потому что запущенная копия держит старый код в памяти, и после
rem обновления её нужно перезапустить.

echo Закрываю запущенные копии OpenTuya Sync...
taskkill /IM OpenTuyaSync.exe /F >nul 2>&1
timeout /t 2 /nobreak >nul

echo Запускаю приложение...
start "" "%~dp0OpenTuyaSync.exe"

echo.
echo Готово. Через несколько секунд проверьте в окне приложения:
echo   внизу должно быть "Служба: подключена"
echo.
echo Если написано "не отвечает" -- пришлите файл:
echo   %~dp0service_link.log
timeout /t 6 /nobreak >nul
