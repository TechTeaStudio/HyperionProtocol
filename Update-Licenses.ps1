<#
.SYNOPSIS
    Замена одного года на другой в файлах лицензий
.DESCRIPTION
    Скрипт рекурсивно ищет файлы лицензий и заменяет старый год на новый
.PARAMETER RootPath
    Корневая директория для поиска (по умолчанию текущая)
.PARAMETER OldYear
    Год который нужно заменить (по умолчанию 2025)
.PARAMETER NewYear
    Новый год (по умолчанию 2026)
.PARAMETER LicenseFileName
    Имя файла лицензии (по умолчанию LICENSE, LICENSE.txt, LICENSE.md)
.PARAMETER WhatIf
    Показать что будет изменено без фактического изменения файлов
.EXAMPLE
    .\Update-Licenses.ps1 -OldYear 2025 -NewYear 2026
.EXAMPLE
    .\Update-Licenses.ps1 -RootPath "C:\Projects" -OldYear 2024 -NewYear 2025
.EXAMPLE
    .\Update-Licenses.ps1 -WhatIf
#>

param(
    [string]$RootPath = ".",
    [int]$OldYear = 2025,
    [int]$NewYear = 2026,
    [string[]]$LicenseFileName = @("LICENSE", "LICENSE.txt", "LICENSE.md", "License.txt"),
    [switch]$WhatIf
)

function Update-LicenseYear {
    param(
        [string]$FilePath,
        [int]$OldYear,
        [int]$NewYear,
        [bool]$DryRun
    )
    
    try {
        # Читаем содержимое файла
        $content = Get-Content -Path $FilePath -Raw -Encoding UTF8
        $originalContent = $content
        
        # Простая замена всех вхождений старого года на новый
        $content = $content -replace $OldYear, $NewYear
        
        # Проверяем, были ли изменения
        if ($content -ne $originalContent) {
            if ($DryRun) {
                Write-Host "  [ПРЕДПРОСМОТР] Будет обновлен: $FilePath" -ForegroundColor Yellow
                Write-Host "  Заменено: $OldYear -> $NewYear" -ForegroundColor Yellow
                
                # Показываем строки с изменениями
                $lines = $originalContent -split "`n"
                $changedLines = @()
                for ($i = 0; $i -lt $lines.Count; $i++) {
                    if ($lines[$i] -match $OldYear) {
                        $changedLines += "    Строка $($i+1): $($lines[$i].Trim())"
                    }
                }
                if ($changedLines.Count -gt 0) {
                    Write-Host "  Затронутые строки:" -ForegroundColor Gray
                    $changedLines | ForEach-Object { Write-Host $_ -ForegroundColor Gray }
                }
            } else {
                # Сохраняем обновленное содержимое
                Set-Content -Path $FilePath -Value $content -Encoding UTF8 -NoNewline
                
                # Подсчитываем количество замен
                $replacements = ([regex]::Matches($originalContent, $OldYear)).Count
                Write-Host "  ✓ Обновлен: $FilePath (замен: $replacements)" -ForegroundColor Green
            }
            return $true
        } else {
            Write-Host "  - Год $OldYear не найден: $FilePath" -ForegroundColor Gray
            return $false
        }
    }
    catch {
        Write-Host "  ✗ Ошибка при обработке $FilePath : $_" -ForegroundColor Red
        return $false
    }
}

# Основная логика
Write-Host "========================================" -ForegroundColor Cyan
Write-Host "Замена $OldYear на $NewYear в лицензиях" -ForegroundColor Cyan
Write-Host "========================================" -ForegroundColor Cyan
Write-Host "Корневая директория: $RootPath"
Write-Host "Режим: $(if($WhatIf){'ПРЕДПРОСМОТР'}else{'ОБНОВЛЕНИЕ'})"
Write-Host ""

# Получаем абсолютный путь
$absolutePath = Resolve-Path -Path $RootPath

# Ищем все файлы лицензий
$licenseFiles = @()
foreach ($fileName in $LicenseFileName) {
    $found = Get-ChildItem -Path $absolutePath -Filter $fileName -Recurse -File -ErrorAction SilentlyContinue
    $licenseFiles += $found
}

if ($licenseFiles.Count -eq 0) {
    Write-Host "Не найдено файлов лицензий в директории $absolutePath" -ForegroundColor Yellow
    exit 0
}

Write-Host "Найдено файлов лицензий: $($licenseFiles.Count)" -ForegroundColor Cyan
Write-Host ""

$updatedCount = 0
$processedCount = 0

foreach ($file in $licenseFiles) {
    $processedCount++
    Write-Host "[$processedCount/$($licenseFiles.Count)] Обработка: $($file.FullName)"
    
    $result = Update-LicenseYear -FilePath $file.FullName -OldYear $OldYear -NewYear $NewYear -DryRun $WhatIf
    if ($result) {
        $updatedCount++
    }
    Write-Host ""
}

# Итоговая статистика
Write-Host "========================================" -ForegroundColor Cyan
Write-Host "Завершено!" -ForegroundColor Green
Write-Host "Обработано файлов: $processedCount"
Write-Host "Обновлено файлов: $updatedCount"
if ($WhatIf) {
    Write-Host ""
    Write-Host "Это был режим предпросмотра. Запустите без -WhatIf для применения изменений." -ForegroundColor Yellow
}
Write-Host "========================================" -ForegroundColor Cyan