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
if (Test-Path $release) { Remove-Item $release -Recurse -Force }

New-Item -ItemType Directory -Path $release -Force | Out-Null

# ---------------------------------------------------- задания сборки
# Каждое задание -- отдельный вывод: так сборка идёт параллельно, а файлы
# не мешают друг другу. Приложение и служба кладутся в одну папку, поэтому
# их вывод должен совпадать -- он и совпадает.
$jobs = @(
    @{ Name = 'приложение';   Project = 'OpenTuyaSync.Agent\OpenTuyaSync.Agent.csproj';   Output = $release }
    @{ Name = 'служба';       Project = 'OpenTuyaSync.Service\OpenTuyaSync.Service.csproj'; Output = $release }
    @{ Name = 'тест подсветки'; Project = 'tools\ColorTest\ColorTest.csproj';             Output = Join-Path $release 'tools\ColorTest' }
    @{ Name = 'CaptureTest';  Project = 'tools\CaptureTest\CaptureTest.csproj';           Output = Join-Path $release 'tools\CaptureTest' }
    @{ Name = 'PipeTest';     Project = 'tools\PipeTest\PipeTest.csproj';                 Output = Join-Path $release 'tools\PipeTest' }
    @{ Name = 'MusicTest';    Project = 'tools\MusicTest\MusicTest.csproj';               Output = Join-Path $release 'tools\MusicTest' }
    @{ Name = 'ConfigTest';   Project = 'tools\ConfigTest\ConfigTest.csproj';             Output = Join-Path $release 'tools\ConfigTest' }
)

Write-Step "Параллельная сборка ($($jobs.Count) проектов, одновременно $MaxParallel)"

$started = Get-Date

# Папки вывода создаём заранее: dotnet publish этого не делает для вложенных
# путей и падает.
foreach ($job in $jobs) {
    New-Item -ItemType Directory -Path $job.Output -Force | Out-Null
}

$running = @()
$failed = @()

foreach ($job in $jobs) {
    # Держим не больше MaxParallel одновременных сборок: больше не ускоряет,
    # но загружает процессор и диск.
    while (@($running | Where-Object { $_.Job.State -eq 'Running' }).Count -ge $MaxParallel) {
        Start-Sleep -Milliseconds 300
    }

    # Задание PowerShell, а не Start-Process: у процесса, запущенного без
    # ожидания, код возврата остаётся недоступным, и проверить результат
    # сборки было бы нельзя. Задание отдаёт и код, и текст ошибки.
    $name = $job.Name
    $project = $job.Project
    $output = $job.Output

    $powerShellJob = Start-Job -ScriptBlock {
        param($WorkingDirectory, $Project, $Output)

        Set-Location $WorkingDirectory

        # Вывод объединяем: сообщения об ошибках dotnet пишет в оба потока.
        $result = & dotnet publish $Project -c Release -o $Output --nologo -v q 2>&1

        [PSCustomObject]@{
            Code   = $LASTEXITCODE
            Output = ($result | Out-String).Trim()
        }
    } -ArgumentList $root, $project, $output

    $running += [PSCustomObject]@{ Name = $name; Job = $powerShellJob }
    Write-Host "  запущено: $name"
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

# Настройки по умолчанию.
#
# config.json намеренно не хранится в репозитории: в нём лежат
# идентификаторы устройств и ключи доступа. Локально файл есть, а на сервере
# сборки его нет, поэтому для релиза создаётся пустой набор настроек --
# пользователь заполняет его через вкладку «Настройки», а недостающие поля
# приложение дополняет значениями по умолчанию.
$configPath = Join-Path $release 'config.json'

if (-not (Test-Path $configPath)) {
    $config = [ordered]@{
        devices               = @()
        auto_start            = $false
        start_minimized       = $false
        restore_scene_on_exit = $false
        active_source         = 'screen'
        auto_start_capture    = $false
    }

    $config | ConvertTo-Json -Depth 5 |
        Set-Content $configPath -Encoding UTF8

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
