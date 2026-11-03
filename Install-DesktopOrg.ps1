<#
.SYNOPSIS
    Installs DesktopOrg for the current Windows user (no Administrator privileges required).
.DESCRIPTION
    1. Migrates saved settings & desktop backups from %LocalAppData%\MyReDesk to %LocalAppData%\DesktopOrg (if needed)
    2. Copies DesktopOrg.exe to %LocalAppData%\Programs\DesktopOrg\DesktopOrg.exe
    3. Creates a Windows Start Menu shortcut (%AppData%\Microsoft\Windows\Start Menu\Programs\DesktopOrg.lnk)
    4. Points Windows Startup (HKCU\...\Run\DesktopOrg) to %LocalAppData%\Programs\DesktopOrg\DesktopOrg.exe --tray
    5. Points the Windows Desktop Right-Click Context Menu to %LocalAppData%\Programs\DesktopOrg\DesktopOrg.exe
    6. Launches the installed DesktopOrg.exe
#>
param(
    [switch]$NoLaunch,
    [switch]$DisableStartup
)

$ErrorActionPreference = "Stop"

# 1. Locate source DesktopOrg.exe (supports running from repo root or from inside dist/)
$candidatePaths = @(
    (Join-Path $PSScriptRoot "dist\DesktopOrg.exe"),
    (Join-Path $PSScriptRoot "DesktopOrg.exe")
)
$sourceExe = $candidatePaths | Where-Object { Test-Path $_ } | Select-Object -First 1

if (-not $sourceExe) {
    Write-Error "Could not find DesktopOrg.exe in '$PSScriptRoot\dist\' or '$PSScriptRoot\'."
    exit 1
}

# 2. Stop any running DesktopOrg or legacy MyReDesk instance before copying
Stop-Process -Name "DesktopOrg" -Force -ErrorAction SilentlyContinue
Stop-Process -Name "MyReDesk" -Force -ErrorAction SilentlyContinue
Start-Sleep -Milliseconds 500

# 3. Migrate saved state & backups from %LocalAppData%\MyReDesk to %LocalAppData%\DesktopOrg if needed
$legacyAppData = Join-Path $env:LOCALAPPDATA "MyReDesk"
$legacyStateFile = Join-Path $legacyAppData "myredesk_state_v2.json"
$newAppData = Join-Path $env:LOCALAPPDATA "DesktopOrg"
$newStateFile = Join-Path $newAppData "desktoporg_state_v2.json"

if (-not (Test-Path $newAppData)) {
    New-Item -ItemType Directory -Path $newAppData -Force | Out-Null
}
if ((Test-Path $legacyStateFile) -and (-not (Test-Path $newStateFile))) {
    Copy-Item -Path $legacyStateFile -Destination $newStateFile -Force
    Write-Host "[Migration] Migrated saved layouts & settings from MyReDesk to DesktopOrg." -ForegroundColor Cyan
}

# Clean up legacy MyReDesk program folder, shortcut, startup, and context menu
$legacyInstallDir = Join-Path $env:LOCALAPPDATA "Programs\MyReDesk"
if (Test-Path $legacyInstallDir) {
    Remove-Item -Path $legacyInstallDir -Recurse -Force -ErrorAction SilentlyContinue
}
$startMenuDir = [Environment]::GetFolderPath("Programs")
$legacyShortcut = Join-Path $startMenuDir "MyReDesk.lnk"
if (Test-Path $legacyShortcut) {
    Remove-Item -Path $legacyShortcut -Force -ErrorAction SilentlyContinue
}
Remove-ItemProperty -Path "HKCU:\Software\Microsoft\Windows\CurrentVersion\Run" -Name "MyReDesk" -ErrorAction SilentlyContinue
Remove-Item -Path "HKCU:\Software\Classes\Directory\Background\shell\MyReDesk" -Recurse -Force -ErrorAction SilentlyContinue

# 4. Copy DesktopOrg.exe to %LocalAppData%\Programs\DesktopOrg\DesktopOrg.exe
$installDir = Join-Path $env:LOCALAPPDATA "Programs\DesktopOrg"
$installedExe = Join-Path $installDir "DesktopOrg.exe"

if (-not (Test-Path $installDir)) {
    New-Item -ItemType Directory -Path $installDir -Force | Out-Null
}

Copy-Item -Path $sourceExe -Destination $installedExe -Force
Write-Host "[1/4] Installed executable to: $installedExe" -ForegroundColor Green

# 5. Create Windows Start Menu shortcut (%AppData%\Microsoft\Windows\Start Menu\Programs\DesktopOrg.lnk)
$shortcutPath = Join-Path $startMenuDir "DesktopOrg.lnk"

$wshShell = New-Object -ComObject WScript.Shell
$shortcut = $wshShell.CreateShortcut($shortcutPath)
$shortcut.TargetPath = $installedExe
$shortcut.WorkingDirectory = $installDir
$shortcut.Description = "DesktopOrg - Desktop Icon and Gray Box Organizer"
$shortcut.IconLocation = "$installedExe,0"
$shortcut.Save()
Write-Host "[2/4] Created Start Menu shortcut: $shortcutPath" -ForegroundColor Green

# 6. Point Windows Startup (HKCU\Software\Microsoft\Windows\CurrentVersion\Run) to installed DesktopOrg.exe
$runKeyPath = "HKCU:\Software\Microsoft\Windows\CurrentVersion\Run"
if (-not $DisableStartup) {
    if (-not (Test-Path $runKeyPath)) {
        New-Item -Path $runKeyPath -Force | Out-Null
    }
    Set-ItemProperty -Path $runKeyPath -Name "DesktopOrg" -Value "`"$installedExe`" --tray"
    Write-Host "[3/4] Registered Windows Startup: `"$installedExe`" --tray" -ForegroundColor Green
} else {
    Remove-ItemProperty -Path $runKeyPath -Name "DesktopOrg" -ErrorAction SilentlyContinue
    Write-Host "[3/4] Skipped Windows Startup registration (-DisableStartup specified)." -ForegroundColor Yellow
}

# 7. Point Windows Desktop Right-Click Context Menu to installed DesktopOrg.exe (Cascading Menu)
$shellKeyPath = "HKCU:\Software\Classes\Directory\Background\shell\DesktopOrg"
if (Test-Path "$shellKeyPath\command") {
    Remove-Item -Path "$shellKeyPath\command" -Recurse -Force -ErrorAction SilentlyContinue
}
New-Item -Path $shellKeyPath -Force | Out-Null
Set-ItemProperty -Path $shellKeyPath -Name "MUIVerb" -Value "DesktopOrg"
Set-ItemProperty -Path $shellKeyPath -Name "SubCommands" -Value ""
Set-ItemProperty -Path $shellKeyPath -Name "Icon" -Value "`"$installedExe`",0"
Set-ItemProperty -Path $shellKeyPath -Name "Position" -Value "Top"
Write-Host "[4/4] Registered Cascading Desktop Right-Click Menu -> $installedExe" -ForegroundColor Green

# 8. Launch installed DesktopOrg.exe via explorer.exe
if (-not $NoLaunch) {
    explorer.exe $installedExe
    Write-Host "`nDesktopOrg is now installed and running!" -ForegroundColor Cyan
}

