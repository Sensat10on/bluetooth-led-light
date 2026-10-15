@echo off
chcp 65001 >nul
cd /d "%~dp0"

rem Полная настройка OpenTuya Sync одним запуском:
rem   1. регистрирует службу (потребуется подтверждение администратора)
rem   2. запускает её
rem   3. ждёт готовности и проверяет связь
rem   4. запускает приложение
rem
rem Запускать двойным щелчком. Права администратора запрашиваются один раз.

setlocal
set DIST=%~dp0
set SERVICE=%DIST%OpenTuyaSync.Service.exe
set AGENT=%DIST%OpenTuyaSync.exe

echo ============================================================
echo   Настройка OpenTuya Sync
echo ============================================================
echo.

if not exist "%SERVICE%" (
  echo ОШИБКА: не найден %SERVICE%
  echo Соберите проект: publish.bat
  pause
  exit /b 1
)

if not exist "%AGENT%" (
  echo ОШИБКА: не найден %AGENT%
  pause
  exit /b 1
)

echo Шаг 1 из 4. Регистрация службы.
echo   Сейчас появится запрос администратора -- нажмите "Да".
echo.
"%SERVICE%" install
if errorlevel 1 (
  echo.
  echo Не удалось зарегистрировать службу.
  echo Запустите этот файл от имени администратора и повторите.
  pause
  exit /b 1
)

echo.
echo Шаг 2 из 4. Запуск службы.
sc start OpenTuyaSync >nul 2>&1
timeout /t 12 /nobreak >nul

echo Шаг 3 из 4. Проверка состояния.
sc query OpenTuyaSync
echo.

echo Шаг 4 из 4. Запуск приложения.
start "" "%AGENT%"

echo.
echo ============================================================
echo   Готово.
echo ============================================================
echo.
echo В окне приложения должно быть "Служба: подключена".
echo Затем: вкладка "Автоматически" -^> "Запустить".
echo.
echo Если статус всё ещё "не отвечает" -- пришлите файл
echo   %DIST%service_link.log
echo.
pause
