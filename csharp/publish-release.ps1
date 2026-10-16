<#
    Сборка релиза: папка Releases и архив с ней.

    Что делает:

      1. останавливает запущенные копии -- иначе файлы заняты и публикация
         падает с ошибкой доступа;
      2. собирает приложение, службу и утилиты ПАРАЛЛЕЛЬНО;
      3. копирует настройки, инструкции, значки и сценарии;
      4. собирает справку из markdown;
      5. упаковывает всё в архив Releases\OpenTuyaSync-v<версия>.zip.

    Почему параллельно. Каждый запуск dotnet publish занимает десятки секунд,
    и последовательная сборка восьми проектов растягивается на минуты.
    Одновременная сборка укладывается примерно в время самого долгого
    проекта: проекты не зависят друг от друга.

    Запуск:
        powershell -NoProfile -ExecutionPolicy Bypass -File publish-release.ps1
        powershell ... -File publish-release.ps1 -Version 1.0.4
#>

param(
    # Версия для имени архива. Если не указана, берётся последний тег git,
    # иначе просто "dev".
    [string]$Version = '',

    # Сколько сборок запускать одновременно.
    [int]$MaxParallel = 4
)

$ErrorActionPreference = 'Stop'

$root = $PSScriptRoot
$release = Join-Path $root 'Releases'
$files = Join-Path $root 'release-files'

function Write-Step([string]$text) {
    Write-Host ''
    Write-Host "== $text" -ForegroundColor Cyan
}

# ---------------------------------------------------------------- версия
if (-not $Version) {
    try {
        $tag = & git -C $root describe --tags --abbrev=0 2>$null
        if ($LASTEXITCODE -eq 0 -and $tag) {
            $Version = ($tag -replace '^v', '').Trim()
        }
    }
    catch {
        # git может быть недоступен -- это не мешает сборке.
    }
}

if (-not $Version) { $Version = 'dev' }

Write-Host "Сборка релиза OpenTuya Sync $Version" -ForegroundColor Green

# ------------------------------------------------- остановка копий
Write-Step 'Остановка запущенных копий'

# Занятые файлы -- самая частая причина сбоя публикации, поэтому копии
# закрываются до всего остального.
foreach ($name in 'OpenTuyaSync', 'OpenTuyaSync.Service', 'OpenTuyaSync.ColorTest') {
    $processes = @(Get-Process -Name $name -ErrorAction SilentlyContinue)
    foreach ($process in $processes) {
        try { Stop-Process -Id $process.Id -Force -ErrorAction Stop } catch { }
    }

    if ($processes.Count -gt 0) {
        Write-Host "  закрыто: $name ($($processes.Count))"
    }
}

# Служба тоже держит файлы, поэтому её останавливаем штатно.
& sc.exe stop OpenTuyaSync 2>$null | Out-Null
Start-Sleep -Seconds 3

# ---------------------------------------------------- очистка
Write-Step 'Очистка папки релиза'

# Настройки сохраняем ДО очистки.
#
# config.json папки релиза -- это рабочие настройки пользователя: список
# устройств, ключи доступа, параметры захвата. Папка очищается целиком,
# поэтому файл на время сборки убирается в сторону и возвращается обратно.
# Без этого каждая пересборка стирала настройки, и всё приходилось
# указывать заново.
$configPath = Join-Path $release 'config.json'
$savedConfig = $null

if (Test-Path $configPath) {
    $savedConfig = Get-Content $configPath -Raw -Encoding UTF8
    Write-Host '  настройки сохранены на время сборки'
}

if (Test-Path $release) { Remove-Item $release -Recurse -Force }

New-Item -ItemType Directory -Path $release -Force | Out-Null

# ---------------------------------------------------- задания сборки
# Проекты делят промежуточные файлы: утилиты зависят от тех же библиотек,
# что и приложение, и компилятор пишет их в общий каталог obj. Если собирать
# такие проекты одновременно, они мешают друг другу: сборка падает с
# «Cannot open ... because it is being used by another process».
#
# Поэтому в два этапа:
#   1. приложение -- оно собирает все общие библиотеки (Core, Capture,
#      Providers, Ipc);
#   2. остальные проекты параллельно с ключом --no-dependencies: их
#      зависимости уже готовы после первого этапа, и объекты никто не делит.
$dependentJobs = @(
    @{ Name = 'служба';         Project = 'OpenTuyaSync.Service\OpenTuyaSync.Service.csproj'; Output = $release }
    @{ Name = 'тест подсветки'; Project = 'tools\ColorTest\ColorTest.csproj';                Output = Join-Path $release 'tools\ColorTest' }
    @{ Name = 'CaptureTest';    Project = 'tools\CaptureTest\CaptureTest.csproj';            Output = Join-Path $release 'tools\CaptureTest' }
    @{ Name = 'PipeTest';       Project = 'tools\PipeTest\PipeTest.csproj';                  Output = Join-Path $release 'tools\PipeTest' }
    @{ Name = 'MusicTest';      Project = 'tools\MusicTest\MusicTest.csproj';                Output = Join-Path $release 'tools\MusicTest' }
    @{ Name = 'ConfigTest';     Project = 'tools\ConfigTest\ConfigTest.csproj';              Output = Join-Path $release 'tools\ConfigTest' }
)

Write-Step "Сборка ($(1 + $dependentJobs.Count) проектов: приложение, затем $($dependentJobs.Count) параллельно)"

$started = Get-Date

# Папки вывода создаём заранее: dotnet publish этого не делает для вложенных
# путей и падает.
New-Item -ItemType Directory -Path $release -Force | Out-Null
foreach ($job in $dependentJobs) {
    New-Item -ItemType Directory -Path $job.Output -Force | Out-Null
}

$failed = @()

# Задание PowerShell, а не Start-Process: у процесса, запущенного без
# ожидания, код возврата остаётся недоступным, и проверить результат сборки
# было бы нельзя. Задание отдаёт и код, и текст ошибки.
function Start-BuildJob {
    param(
        [string]$Name,
        [string]$Project,
        [string]$Output,
        [switch]$NoDependencies
    )

    $job = Start-Job -ScriptBlock {
        param($WorkingDirectory, $Project, $Output, $NoDependencies)

        Set-Location $WorkingDirectory

        $arguments = @('publish', $Project, '-c', 'Release', '-o', $Output, '--nologo', '-v', 'q')

        if ($NoDependencies) { $arguments += '--no-dependencies' }

        # Вывод объединяем: сообщения об ошибках dotnet пишет в оба потока.
        $result = & dotnet @arguments 2>&1

        [PSCustomObject]@{
            Code   = $LASTEXITCODE
            Output = ($result | Out-String).Trim()
        }
    } -ArgumentList $root, $Project, $Output, $NoDependencies.IsPresent

    Write-Host "  запущено: $Name"

    return [PSCustomObject]@{ Name = $Name; Job = $job }
}

# --- Этап 1: приложение. Собирает общие библиотеки для всех остальных.
Write-Host ''
Write-Host '  этап 1: приложение (общие библиотеки)'

$appJob = Start-BuildJob -Name 'приложение' `
    -Project 'OpenTuyaSync.Agent\OpenTuyaSync.Agent.csproj' `
    -Output $release

$appJob.Job | Wait-Job | Out-Null
$appResult = Receive-Job -Job $appJob.Job
Remove-Job -Job $appJob.Job -Force

$appCode = if ($appResult -is [array]) { $appResult[0].Code } else { $appResult.Code }
$appText = if ($appResult -is [array]) { $appResult[0].Output } else { $appResult.Output }

if ($appCode -ne 0) {
    $failed += 'приложение'
    Write-Host "  ОШИБКА: приложение (код $appCode)" -ForegroundColor Red

    if ($appText) {
        foreach ($line in ($appText -split "`r?`n" | Select-Object -First 8)) {
            Write-Host "      $line" -ForegroundColor DarkYellow
        }
    }

    throw 'Сборка приложения не удалась, остальные проекты собирать нечем'
}

Write-Host '  готово:  приложение' -ForegroundColor Green

# --- Этап 2: остальные проекты параллельно.
Write-Host ''
Write-Host "  этап 2: остальные проекты, одновременно $MaxParallel"

$running = @()

foreach ($job in $dependentJobs) {
    # Держим не больше MaxParallel одновременных сборок: больше не ускоряет,
    # но загружает процессор и диск.
    while (@($running | Where-Object { $_.Job.State -eq 'Running' }).Count -ge $MaxParallel) {
        Start-Sleep -Milliseconds 300
    }

    $running += Start-BuildJob -Name $job.Name -Project $job.Project `
        -Output $job.Output -NoDependencies
}

# Ждём завершения всех сборок.
if ($running.Count -gt 0) {
    $running | ForEach-Object { $_.Job } | Wait-Job | Out-Null
}

foreach ($item in $running) {
    $result = Receive-Job -Job $item.Job
    Remove-Job -Job $item.Job -Force

    $code = if ($result -is [array]) { $result[0].Code } else { $result.Code }
    $text = if ($result -is [array]) { $result[0].Output } else { $result.Output }

    if ($code -ne 0) {
        $failed += $item.Name
        Write-Host "  ОШИБКА: $($item.Name) (код $code)" -ForegroundColor Red

        # Показываем причину: без неё непонятно, что именно не собралось.
        if ($text) {
            foreach ($line in ($text -split "`r?`n" | Select-Object -First 6)) {
                Write-Host "      $line" -ForegroundColor DarkYellow
            }
        }
    }
    else {
        Write-Host "  готово:  $($item.Name)" -ForegroundColor Green
    }
}

$elapsed = ((Get-Date) - $started).TotalSeconds
Write-Host ''
Write-Host ("  сборка заняла {0:0} с" -f $elapsed)

if ($failed.Count -gt 0) {
    throw "Не удалось собрать: $($failed -join ', ')"
}

# ---------------------------------------------------- вспомогательные файлы
Write-Step 'Подготовка вспомогательных файлов'

# Отладочный мусор в релиз не попадает.
Get-ChildItem $release -Recurse -Include '*.pdb' -ErrorAction SilentlyContinue |
    Remove-Item -Force -ErrorAction SilentlyContinue

foreach ($junk in 'agent_alive.txt', 'service_link.log', 'colortest_alive.txt',
                  'colortest_crash.log', 'selftest_report.txt', 'ui_test_data') {
    Get-ChildItem $release -Recurse -Filter $junk -ErrorAction SilentlyContinue |
        Remove-Item -Recurse -Force -ErrorAction SilentlyContinue
}

Get-ChildItem $release -Recurse -Filter 'selftest_*.png' -ErrorAction SilentlyContinue |
    Remove-Item -Force -ErrorAction SilentlyContinue

# Настройки, инструкции, значки и сценарии. Русские имена берутся из папки
# как есть, поэтому в самом скрипте нет не-ASCII текста.
foreach ($pattern in (Join-Path $files 'config.json'),
                     (Join-Path $files '*.bat'),
                     (Join-Path $files '*.txt'),
                     (Join-Path $files '*.ps1'),
                     (Join-Path $root 'assets\icon.ico')) {
    Get-ChildItem $pattern -ErrorAction SilentlyContinue | ForEach-Object {
        Copy-Item $_.FullName -Destination $release -Force
    }
}

# Настройки: возвращаем сохранённые либо создаём пустые.
#
# config.json намеренно не хранится в репозитории: в нём лежат
# идентификаторы устройств и ключи доступа. Поэтому:
#   - локально возвращаются настройки пользователя, снятые до очистки;
#   - на сервере сборки файла нет, и создаётся пустой набор -- пользователь
#     заполняет его через вкладку «Настройки», а недостающие поля приложение
#     дополняет значениями по умолчанию.
if ($savedConfig) {
    # Запись без метки порядка байтов: метка не нужна и мешает некоторым
    # библиотекам читать файл.
    [System.IO.File]::WriteAllText($configPath, $savedConfig, (New-Object System.Text.UTF8Encoding($false)))
    Write-Host '  настройки возвращены на место'
}
elseif (-not (Test-Path $configPath)) {
    $config = [ordered]@{
        devices                  = @()
        auto_start               = $false
        start_minimized          = $false
        restore_scene_on_exit    = $false
        active_source            = 'screen'
        auto_start_capture       = $false

        # Перезапуск Bluetooth-адаптера выключен: он разрывает связь со всеми
        # Bluetooth-устройствами компьютера, включая пульты других устройств.
        allow_bluetooth_recovery = $false
    }

    $json = $config | ConvertTo-Json -Depth 5
    [System.IO.File]::WriteAllText($configPath, $json, (New-Object System.Text.UTF8Encoding($false)))

    Write-Host '  создан пустой config.json'
}

# Справка для пользователя: markdown преобразуется в обычный текст.
& powershell -NoProfile -ExecutionPolicy Bypass -File (Join-Path $root 'make-guide.ps1') -Output $release
if ($LASTEXITCODE -ne 0) { throw 'Не удалось собрать справку' }

# ---------------------------------------------------- архив
Write-Step 'Упаковка архива'

$zip = Join-Path $release "OpenTuyaSync-v$Version.zip"
Remove-Item $zip -Force -ErrorAction SilentlyContinue

Compress-Archive -Path (Join-Path $release '*') -DestinationPath $zip -CompressionLevel Optimal

$zipSize = [math]::Round((Get-Item $zip).Length / 1MB, 2)
$folderSize = [math]::Round(((Get-ChildItem $release -Recurse -File |
    Measure-Object -Property Length -Sum).Sum) / 1MB, 1)
$fileCount = (Get-ChildItem $release -Recurse -File).Count

Write-Host ''
Write-Host '============================================================' -ForegroundColor Green
Write-Host "  Готово: OpenTuya Sync $Version" -ForegroundColor Green
Write-Host '============================================================' -ForegroundColor Green
Write-Host "  папка:  $release"
Write-Host "  архив:  $zip ($zipSize МБ)"
Write-Host "  всего:  $fileCount файлов, $folderSize МБ"
