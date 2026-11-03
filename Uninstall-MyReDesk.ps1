<#
.SYNOPSIS
    Uninstalls MyReDesk from %LocalAppData%\Programs\MyReDesk, Start Menu, Windows Startup, and Desktop Right-Click Menu.
#>
param(
    [switch]$DeleteSavedLayoutsAndSettings
)

$ErrorActionPreference = "SilentlyContinue"

Stop-Process -Name "MyReDesk" -Force -ErrorAction SilentlyContinue
Start-Sleep -Milliseconds 400

# 1. Remove Windows Startup entry
Remove-ItemProperty -Path "HKCU:\Software\Microsoft\Windows\CurrentVersion\Run" -Name "MyReDesk" -ErrorAction SilentlyContinue
Write-Host "[1/4] Removed Windows Startup entry." -ForegroundColor Yellow

# 2. Remove Desktop Right-Click Context Menu entry
Remove-Item -Path "HKCU:\Software\Classes\Directory\Background\shell\MyReDesk" -Recurse -Force -ErrorAction SilentlyContinue
Write-Host "[2/4] Removed Desktop Right-Click Menu entry." -ForegroundColor Yellow

# 3. Remove Start Menu shortcut
$shortcutPath = Join-Path ([Environment]::GetFolderPath("Programs")) "MyReDesk.lnk"
if (Test-Path $shortcutPath) {
    Remove-Item -Path $shortcutPath -Force
}
Write-Host "[3/4] Removed Start Menu shortcut." -ForegroundColor Yellow

# 4. Remove %LocalAppData%\Programs\MyReDesk
$installDir = Join-Path $env:LOCALAPPDATA "Programs\MyReDesk"
if (Test-Path $installDir) {
    Remove-Item -Path $installDir -Recurse -Force
}
Write-Host "[4/4] Removed installed binary from $installDir." -ForegroundColor Yellow

if ($DeleteSavedLayoutsAndSettings) {
    $appDataDir = Join-Path $env:LOCALAPPDATA "MyReDesk"
    if (Test-Path $appDataDir) {
        Remove-Item -Path $appDataDir -Recurse -Force
        Write-Host "Removed saved settings and layouts in $appDataDir." -ForegroundColor Yellow
    }
} else {
    Write-Host "Preserved your saved desktop backups in $env:LOCALAPPDATA\MyReDesk (pass -DeleteSavedLayoutsAndSettings to delete)." -ForegroundColor Cyan
}

