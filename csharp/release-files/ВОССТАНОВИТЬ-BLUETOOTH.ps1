<#
    Перезапуск Bluetooth-адаптера.

    Зачем: Windows иногда «залипает» на устройстве и отвечает «отказ в
    доступе» на службу ленты, хотя устройство видно и числится подключённым.
    Перезапуск службы Bluetooth в этом случае не помогает: сбрасывать нужно
    сам адаптер. Именно это и делает скрипт.

    Нужны права администратора: управление устройствами требует их. Если
    прав нет, скрипт перезапустит себя с запросом -- появится окно «Разрешить
    внесение изменений», нужно нажать «Да».

    Запуск:
        powershell -NoProfile -ExecutionPolicy Bypass -File restart-bluetooth.ps1
#>

param(
    # Сколько секунд ждать после выключения и после включения адаптера.
    [int]$PauseSeconds = 4
)

$ErrorActionPreference = 'Stop'

function Test-Admin {
    $identity = [Security.Principal.WindowsIdentity]::GetCurrent()
    $principal = New-Object Security.Principal.WindowsPrincipal($identity)
    return $principal.IsInRole([Security.Principal.WindowsBuiltInRole]::Administrator)
}

# Без прав перезапускаем себя с запросом повышения.
if (-not (Test-Admin)) {
    Write-Host 'Нужны права администратора: появится запрос, нажмите "Да".' -ForegroundColor Yellow

    $arguments = @(
        '-NoProfile'
        '-ExecutionPolicy', 'Bypass'
        '-File', "`"$PSCommandPath`""
        '-PauseSeconds', $PauseSeconds
    )

    try {
        $process = Start-Process -FilePath 'powershell.exe' -ArgumentList $arguments `
            -Verb RunAs -PassThru -Wait

        exit $process.ExitCode
    }
    catch {
        Write-Host 'Запрос прав отклонён -- перезапустить адаптер не удалось.' -ForegroundColor Red
        exit 1
    }
}

Write-Host '=== Перезапуск Bluetooth-адаптера ===' -ForegroundColor Cyan
Write-Host ''
Write-Host 'ВНИМАНИЕ: на время перезапуска отключатся ВСЕ Bluetooth-устройства' -ForegroundColor Yellow
Write-Host 'компьютера: мышь, клавиатура, наушники. Могут отключиться и пульты' -ForegroundColor Yellow
Write-Host 'других устройств, например Bluetooth-пульт приставки Xiaomi:' -ForegroundColor Yellow
Write-Host 'приставка теряет пульт, когда компьютер перезапускает своё радио.' -ForegroundColor Yellow
Write-Host ''
Write-Host 'Если лента просто занята телефоном, этот сценарий не нужен:' -ForegroundColor Yellow
Write-Host 'освободите ленту и нажмите «Переподключить устройства» в приложении.' -ForegroundColor Yellow
Write-Host ''

# Адаптеры -- это устройства класса Bluetooth с типом Radio.
# Служебные устройства (Enumerator, RFCOMM, профили телефонов) не трогаем:
# их перезапуск ничего не даёт, а связь с телефоном оборвёт.
$adapters = @(Get-PnpDevice -Class Bluetooth -ErrorAction SilentlyContinue |
    Where-Object {
        $_.FriendlyName -match 'Radio|Adapter|Адаптер' -and
        $_.FriendlyName -notmatch 'Enumerator|RFCOMM|Перечисление'
    })

if ($adapters.Count -eq 0) {
    Write-Host 'Адаптер Bluetooth не найден.' -ForegroundColor Red
    exit 1
}

foreach ($adapter in $adapters) {
    Write-Host "Адаптер: $($adapter.FriendlyName)"
}

Write-Host ''
Write-Host 'Выключаю...'
foreach ($adapter in $adapters) {
    try {
        Disable-PnpDevice -InstanceId $adapter.InstanceId -Confirm:$false -ErrorAction Stop
        Write-Host "  выключен: $($adapter.FriendlyName)"
    }
    catch {
        Write-Host "  не удалось выключить $($adapter.FriendlyName): $($_.Exception.Message)" -ForegroundColor Yellow
    }
}

Start-Sleep -Seconds $PauseSeconds

Write-Host 'Включаю...'
foreach ($adapter in $adapters) {
    try {
        Enable-PnpDevice -InstanceId $adapter.InstanceId -Confirm:$false -ErrorAction Stop
        Write-Host "  включён: $($adapter.FriendlyName)"
    }
    catch {
        Write-Host "  не удалось включить $($adapter.FriendlyName): $($_.Exception.Message)" -ForegroundColor Yellow
    }
}

# Даём стеку Bluetooth подняться и найти устройства заново.
Start-Sleep -Seconds ($PauseSeconds + 4)

Write-Host ''
Write-Host 'Служба Bluetooth:'
Get-Service bthserv -ErrorAction SilentlyContinue |
    Select-Object Name, Status | Format-Table -AutoSize

Write-Host 'Готово. Вернитесь в приложение и нажмите «Переподключить устройства».' -ForegroundColor Green
