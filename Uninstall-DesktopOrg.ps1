<#
.SYNOPSIS
    Uninstalls DesktopOrg from %LocalAppData%\Programs\DesktopOrg, Start Menu, Windows Startup, and Desktop Right-Click Menu.
#>
param(
    [switch]$DeleteSavedLayoutsAndSettings
)

$ErrorActionPreference = "SilentlyContinue"

Stop-Process -Name "DesktopOrg" -Force -ErrorAction SilentlyContinue
Stop-Process -Name "MyReDesk" -Force -ErrorAction SilentlyContinue
Start-Sleep -Milliseconds 400

# 1. Remove Windows Startup entry
Remove-ItemProperty -Path "HKCU:\Software\Microsoft\Windows\CurrentVersion\Run" -Name "DesktopOrg" -ErrorAction SilentlyContinue
Remove-ItemProperty -Path "HKCU:\Software\Microsoft\Windows\CurrentVersion\Run" -Name "MyReDesk" -ErrorAction SilentlyContinue
Write-Host "[1/4] Removed Windows Startup entry." -ForegroundColor Yellow

# 2. Remove Desktop Right-Click Context Menu entry
Remove-Item -Path "HKCU:\Software\Classes\Directory\Background\shell\DesktopOrg" -Recurse -Force -ErrorAction SilentlyContinue
Remove-Item -Path "HKCU:\Software\Classes\Directory\Background\shell\MyReDesk" -Recurse -Force -ErrorAction SilentlyContinue
Write-Host "[2/4] Removed Desktop Right-Click Menu entry." -ForegroundColor Yellow

# 3. Remove Start Menu shortcut
$startMenuDir = [Environment]::GetFolderPath("Programs")
foreach ($lnk in @("DesktopOrg.lnk", "MyReDesk.lnk")) {
    $shortcutPath = Join-Path $startMenuDir $lnk
    if (Test-Path $shortcutPath) {
        Remove-Item -Path $shortcutPath -Force
    }
}
Write-Host "[3/4] Removed Start Menu shortcut." -ForegroundColor Yellow

# 4. Remove %LocalAppData%\Programs\DesktopOrg
foreach ($dirName in @("Programs\DesktopOrg", "Programs\MyReDesk")) {
    $installDir = Join-Path $env:LOCALAPPDATA $dirName
    if (Test-Path $installDir) {
        Remove-Item -Path $installDir -Recurse -Force
    }
}
Write-Host "[4/4] Removed installed binary from $env:LOCALAPPDATA\Programs\DesktopOrg." -ForegroundColor Yellow

if ($DeleteSavedLayoutsAndSettings) {
    foreach ($dataDir in @("DesktopOrg", "MyReDesk")) {
        $appDataDir = Join-Path $env:LOCALAPPDATA $dataDir
        if (Test-Path $appDataDir) {
            Remove-Item -Path $appDataDir -Recurse -Force
            Write-Host "Removed saved settings and layouts in $appDataDir." -ForegroundColor Yellow
        }
    }
} else {
    Write-Host "Preserved your saved desktop backups in $env:LOCALAPPDATA\DesktopOrg (pass -DeleteSavedLayoutsAndSettings to delete)." -ForegroundColor Cyan
}

