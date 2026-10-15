@echo off
chcp 65001 >nul
cd /d "%~dp0"

rem Публикация приложения и службы в одну папку dist.
rem
rem --self-contained false: используется установленный .NET. Вариант с
rem   включённым рантаймом весит около 150 МБ на каждое приложение, а
rem   выигрыша в скорости не даёт.
rem --single-file не используется намеренно: он распаковывает содержимое
rem   во временную папку при каждом запуске, что заметно удлиняет старт.

if not exist dist mkdir dist

echo Публикация приложения-агента...
dotnet publish OpenTuyaSync.Agent\OpenTuyaSync.Agent.csproj ^
  -c Release -o dist --nologo -v q
if errorlevel 1 goto fail

echo Публикация службы...
dotnet publish OpenTuyaSync.Service\OpenTuyaSync.Service.csproj ^
  -c Release -o dist --nologo -v q
if errorlevel 1 goto fail

echo Копирование значка...
copy /y assets\icon.ico dist\icon.ico >nul

echo.
echo Готово. Файлы в папке dist:
echo   OpenTuyaSync.exe          -- приложение (интерфейс и захват экрана)
echo   OpenTuyaSync.Service.exe  -- служба (управление устройствами)
echo.
echo Установка службы (нужны права администратора):
echo   OpenTuyaSync.Service.exe install
echo.
echo Установка службы с предустановленными устройствами:
echo   OpenTuyaSync.Service.exe install --preset "Кухня^|tuya^|DEVICEID^|192.168.1.50^|LOCALKEY^|3.5"
echo.
echo Диагностика:
echo   OpenTuyaSync.Service.exe --console   -- служба с выводом в консоль
echo   tools\CaptureTest                     -- проверка захвата экрана
echo   tools\PipeTest                        -- проверка связи со службой
pause
exit /b 0

:fail
echo.
echo Публикация не удалась. Проверьте вывод выше.
pause
exit /b 1
