# Создаёт самоподписанный сертификат подписи кода и подписывает им сборку.
#
# Зачем: Windows SmartScreen предупреждает о запуске неподписанных
# приложений. После подписи и установки сертификата в доверенные корневые
# ЭТОЙ учётной записи предупреждение исчезает.
#
# Права администратора не нужны: сертификат ставится в хранилище текущего
# пользователя (CurrentUser), а не в системное (LocalMachine).
#
# ВАЖНО понимать ограничение: для других людей это не сработает. Чтобы
# предупреждение не появлялось на чужом компьютере, сертификат должен быть
# выдан удостоверяющим центром (платно). Самоподписанный годится для себя
# и для тестовых машин, где вы сами установите сертификат.
#
# Запуск:  powershell -ExecutionPolicy Bypass -File sign-selfsigned.ps1

param(
    [string]$DistPath = (Join-Path $PSScriptRoot 'dist'),
    [string]$Subject = 'CN=OpenTuya Sync Self-Signed',
    [switch]$Rollback
)

$ErrorActionPreference = 'Stop'

function Remove-Certificate {
    param([string]$CertSubject)

    Write-Host '=== Удаление сертификата ===' -ForegroundColor Cyan
    $removed = 0

    foreach ($storeName in 'My', 'Root') {
        $store = New-Object System.Security.Cryptography.X509Certificates.X509Store($storeName, 'CurrentUser')
        $store.Open('ReadWrite')

        $found = $store.Certificates | Where-Object { $_.Subject -eq $CertSubject }
        foreach ($cert in $found) {
            $store.Remove($cert)
            Write-Host "  удалён из $storeName : $($cert.Thumbprint)"
            $removed++
        }

        $store.Close()
    }

    if ($removed -eq 0) {
        Write-Host '  сертификат не найден'
    } else {
        Write-Host ''
        Write-Host 'Файлы остались подписанными, но подпись больше не действительна.' -ForegroundColor Yellow
        Write-Host 'Пересоберите проект (publish.bat), чтобы получить неподписанные файлы.' -ForegroundColor Yellow
    }
}

if ($Rollback) {
    Remove-Certificate -CertSubject $Subject
    exit 0
}

if (-not (Test-Path $DistPath)) {
    Write-Host "Папка не найдена: $DistPath" -ForegroundColor Red
    Write-Host 'Сначала выполните publish.bat' -ForegroundColor Yellow
    exit 1
}

$files = @(Get-ChildItem $DistPath -Filter '*.exe')
if ($files.Count -eq 0) {
    Write-Host "В папке нет исполняемых файлов: $DistPath" -ForegroundColor Red
    exit 1
}

Write-Host '=== Создание сертификата ===' -ForegroundColor Cyan

$existing = Get-ChildItem Cert:\CurrentUser\My |
    Where-Object { $_.Subject -eq $Subject } |
    Select-Object -First 1

if ($existing) {
    Write-Host "  уже существует: $($existing.Thumbprint)"
    $cert = $existing
} else {
    $cert = New-SelfSignedCertificate `
        -Type CodeSigningCert `
        -Subject $Subject `
        -CertStoreLocation 'Cert:\CurrentUser\My' `
        -NotAfter (Get-Date).AddYears(5) `
        -KeyUsage DigitalSignature `
        -KeyAlgorithm RSA `
        -KeyLength 2048

    Write-Host "  создан: $($cert.Thumbprint)"
}

Write-Host ''
Write-Host '=== Установка в доверенные корневые (текущий пользователь) ===' -ForegroundColor Cyan

$rootStore = New-Object System.Security.Cryptography.X509Certificates.X509Store('Root', 'CurrentUser')
$rootStore.Open('ReadWrite')

$alreadyTrusted = $rootStore.Certificates | Where-Object { $_.Thumbprint -eq $cert.Thumbprint }
if ($alreadyTrusted) {
    Write-Host '  уже в доверенных корневых'
} else {
    $rootStore.Add($cert)
    Write-Host '  добавлен в доверенные корневые'
}

$rootStore.Close()

Write-Host ''
Write-Host '=== Подпись исполняемых файлов ===' -ForegroundColor Cyan

foreach ($file in $files) {
    $result = Set-AuthenticodeSignature -FilePath $file.FullName -Certificate $cert -HashAlgorithm SHA256
    Write-Host "  $($file.Name): $($result.Status)"
}

Write-Host ''
Write-Host '=== Проверка ===' -ForegroundColor Cyan

$allValid = $true
foreach ($file in $files) {
    $status = Get-AuthenticodeSignature $file.FullName
    $ok = $status.Status -eq 'Valid'
    if (-not $ok) { $allValid = $false }

    Write-Host "  $($file.Name): $($status.Status)" -ForegroundColor $(if ($ok) { 'Green' } else { 'Yellow' })
    Write-Host "    издатель: $($status.SignerCertificate.Subject)"
}

Write-Host ''
if ($allValid) {
    Write-Host 'Готово. Подписи действительны.' -ForegroundColor Green
    Write-Host 'На этой машине предупреждение SmartScreen при запуске исчезнет.'
} else {
    Write-Host 'Часть подписей недействительна -- проверьте вывод выше.' -ForegroundColor Yellow
}

Write-Host ''
Write-Host 'Откат (удалить сертификат):' -ForegroundColor Gray
Write-Host '  powershell -ExecutionPolicy Bypass -File sign-selfsigned.ps1 -Rollback' -ForegroundColor Gray
