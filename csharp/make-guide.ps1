<#
    Преобразует СПРАВКА.md в СПРАВКА.txt для релиза.

    В репозитории справка лежит в разметке markdown: так её удобно читать на
    GitHub. В релизе рядом с программой нужен обычный текст, который
    откроется в блокноте без лишних символов.

    Запуск: powershell -NoProfile -File make-guide.ps1 -Output ..\Releases
#>

param(
    [Parameter(Mandatory = $true)]
    [string]$Output
)

$ErrorActionPreference = 'Stop'

$source = Join-Path $PSScriptRoot 'СПРАВКА.md'
if (-not (Test-Path $source)) {
    Write-Error "Не найден файл справки: $source"
}

$text = Get-Content -Raw -Encoding UTF8 $source

# Содержание со ссылками: в текстовом файле ссылки не работают, поэтому
# раздел убираем -- разделы и так идут по порядку ниже.
$text = $text -replace '(?s)## Содержание.*?(?=\r?\n---)', ''

# Заголовки: убираем решётки, оставляем текст.
$text = $text -replace '(?m)^#{1,6}\s*', ''

# Ссылки вида [текст](адрес) превращаем в текст.
$text = $text -replace '\[([^\]]+)\]\([^)]*\)', '$1'

# Выделение жирным и код в обратных кавычках.
$text = $text -replace '\*\*', ''
$text = $text -replace '`', ''

# Горизонтальные линии не нужны.
$text = $text -replace '(?m)^---+\s*$', ('-' * 70)

# Таблицы: разделители строк вида |---|---| убираем совсем.
$text = $text -replace '(?m)^\|[\s\-:|]+\|\s*$', ''

# Разметка таблиц: убираем крайние вертикальные черты, столбцы разделяем.
$text = $text -replace '(?m)^\|\s*', '  '
$text = $text -replace '(?m)\s*\|\s*$', ''
$text = $text -replace '\s\|\s', '   '

# Пустые строки в конце.
$text = $text.TrimEnd() + "`r`n"

$target = Join-Path $Output 'СПРАВКА.txt'
[System.IO.File]::WriteAllText($target, $text, (New-Object System.Text.UTF8Encoding($true)))

$size = [math]::Round((Get-Item $target).Length / 1KB, 1)
Write-Output "Справка записана: $target ($size КБ)"
