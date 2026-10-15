@echo off
chcp 65001 >nul
cd /d "%~dp0"

rem Собирает утилиты диагностики и тестовые программы в папку tools.
rem
rem   CaptureTest   -- работает ли захват экрана (DXGI/GDI) и сколько ест CPU
rem   PipeTest      -- отвечает ли служба и как быстро проходит кадр цвета
rem   DxgiProbe     -- доступно ли дублирование рабочего стола в этой системе
rem   BleTest       -- что видно по Bluetooth и как подключается лента
rem   UiTest        -- строится ли окно приложения без ошибок
rem   ReconnectTest -- восстанавливается ли связь после перезапуска службы
rem   ColorTest     -- заливка экрана для проверки подсветки

echo Сборка утилит...
echo.

for %%T in (CaptureTest PipeTest DxgiProbe BleTest UiTest ReconnectTest ColorTest) do (
  echo   %%T
  dotnet build "tools\%%T\%%T.csproj" -c Release --nologo -v q
  if errorlevel 1 goto fail
)

echo.
echo Готово.
echo.
echo   Проверка захвата экрана:
echo     tools\CaptureTest\bin\Release\net10.0-windows\OpenTuyaSync.CaptureTest.exe
echo   Проверка связи со службой:
echo     tools\PipeTest\bin\Release\net10.0-windows\OpenTuyaSync.PipeTest.exe ping
echo   Проверка Bluetooth-ленты:
echo     tools\BleTest\bin\Release\net10.0-windows10.0.19041.0\OpenTuyaSync.BleTest.exe scan
echo   Проверка подключения ленты кодом приложения:
echo     tools\ReconnectTest\bin\Release\net10.0-windows10.0.19041.0\OpenTuyaSync.ReconnectTest.exe ble
echo   Проверка построения окна:
echo     tools\UiTest\bin\Release\net10.0-windows10.0.19041.0\OpenTuyaSync.UiTest.exe
echo   Тест подсветки на весь экран:
echo     tools\ColorTest\bin\Release\net10.0-windows\OpenTuyaSync.ColorTest.exe
echo.
pause
exit /b 0

:fail
echo.
echo Сборка утилит не удалась.
pause
exit /b 1
