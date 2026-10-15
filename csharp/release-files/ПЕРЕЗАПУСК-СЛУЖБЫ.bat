@echo off
chcp 65001 >nul
cd /d "%~dp0"

rem Перезапуск службы OpenTuyaSync.
rem
rem Отдельный сценарий нужен потому, что остановка службы требует прав
rem администратора, а обычный ПЕРЕЗАПУСК.bat их не запрашивает и службу не
rem трогает -- он перезапускает только окно приложения.
rem
rem Запускать двойным щелчком. Запрос администратора появится один раз.

setlocal
set SERVICE_EXE=%~dp0OpenTuyaSync.Service.exe

echo ============================================================
echo   Перезапуск службы OpenTuyaSync
echo ============================================================
echo.

if not exist "%SERVICE_EXE%" (
  echo ОШИБКА: не найден %SERVICE_EXE%
  echo Запустите этот файл из папки с программой.
  pause
  exit /b 1
)

rem Проверяем права: без них остановка службы не пройдёт.
net session >nul 2>&1
if errorlevel 1 (
  echo Нужны права администратора. Сейчас появится запрос -- нажмите "Да".
  echo.
  powershell -NoProfile -Command "Start-Process -FilePath '%~f0' -Verb RunAs"
  exit /b 0
)

echo Шаг 1 из 3. Остановка службы.
sc stop OpenTuyaSync >nul 2>&1
timeout /t 8 /nobreak >nul

echo Шаг 2 из 3. Запуск службы.
sc start OpenTuyaSync >nul 2>&1
timeout /t 10 /nobreak >nul

echo Шаг 3 из 3. Состояние.
sc query OpenTuyaSync | findstr /C:"STATE"

echo.
echo Готово. Проверьте связь в приложении: внизу должно быть
echo "Служба: подключена".
echo.
echo Если служба не запустилась, посмотрите журнал Windows:
echo   Просмотр событий -^> Журналы Windows -^> Приложение, источник OpenTuyaSync
echo.
pause
