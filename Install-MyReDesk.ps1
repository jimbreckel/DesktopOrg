<#
.SYNOPSIS
    Installs MyReDesk for the current Windows user (no Administrator privileges required).
.DESCRIPTION
    1. Copies MyReDesk.exe to %LocalAppData%\Programs\MyReDesk\MyReDesk.exe
    2. Creates a Windows Start Menu shortcut (%AppData%\Microsoft\Windows\Start Menu\Programs\MyReDesk.lnk)
    3. Points Windows Startup (HKCU\...\Run\MyReDesk) to %LocalAppData%\Programs\MyReDesk\MyReDesk.exe --tray
    4. Points the Windows Desktop Right-Click Context Menu to %LocalAppData%\Programs\MyReDesk\MyReDesk.exe
    5. Launches the installed MyReDesk.exe
#>
param(
    [switch]$NoLaunch,
    [switch]$DisableStartup
)

$ErrorActionPreference = "Stop"

# 1. Locate source MyReDesk.exe (supports running from repo root or from inside dist/)
$candidatePaths = @(
    (Join-Path $PSScriptRoot "dist\MyReDesk.exe"),
    (Join-Path $PSScriptRoot "MyReDesk.exe")
)
$sourceExe = $candidatePaths | Where-Object { Test-Path $_ } | Select-Object -First 1

if (-not $sourceExe) {
    Write-Error "Could not find MyReDesk.exe in '$PSScriptRoot\dist\' or '$PSScriptRoot\'."
    exit 1
}

# 2. Stop any running MyReDesk instance before copying
Stop-Process -Name "MyReDesk" -Force -ErrorAction SilentlyContinue
Start-Sleep -Milliseconds 400

# 3. Copy MyReDesk.exe to %LocalAppData%\Programs\MyReDesk\MyReDesk.exe
$installDir = Join-Path $env:LOCALAPPDATA "Programs\MyReDesk"
$installedExe = Join-Path $installDir "MyReDesk.exe"

if (-not (Test-Path $installDir)) {
    New-Item -ItemType Directory -Path $installDir -Force | Out-Null
}

Copy-Item -Path $sourceExe -Destination $installedExe -Force
Write-Host "[1/4] Installed executable to: $installedExe" -ForegroundColor Green

# 4. Create Windows Start Menu shortcut (%AppData%\Microsoft\Windows\Start Menu\Programs\MyReDesk.lnk)
$startMenuDir = [Environment]::GetFolderPath("Programs")
$shortcutPath = Join-Path $startMenuDir "MyReDesk.lnk"

$wshShell = New-Object -ComObject WScript.Shell
$shortcut = $wshShell.CreateShortcut($shortcutPath)
$shortcut.TargetPath = $installedExe
$shortcut.WorkingDirectory = $installDir
$shortcut.Description = "MyReDesk - Desktop Icon and Gray Box Organizer"
$shortcut.IconLocation = "$installedExe,0"
$shortcut.Save()
Write-Host "[2/4] Created Start Menu shortcut: $shortcutPath" -ForegroundColor Green

# 5. Point Windows Startup (HKCU\Software\Microsoft\Windows\CurrentVersion\Run) to installed MyReDesk.exe
$runKeyPath = "HKCU:\Software\Microsoft\Windows\CurrentVersion\Run"
if (-not $DisableStartup) {
    if (-not (Test-Path $runKeyPath)) {
        New-Item -Path $runKeyPath -Force | Out-Null
    }
    Set-ItemProperty -Path $runKeyPath -Name "MyReDesk" -Value "`"$installedExe`" --tray"
    Write-Host "[3/4] Registered Windows Startup: `"$installedExe`" --tray" -ForegroundColor Green
} else {
    Remove-ItemProperty -Path $runKeyPath -Name "MyReDesk" -ErrorAction SilentlyContinue
    Write-Host "[3/4] Skipped Windows Startup registration (-DisableStartup specified)." -ForegroundColor Yellow
}

# 6. Point Windows Desktop Right-Click Context Menu to installed MyReDesk.exe
$shellKeyPath = "HKCU:\Software\Classes\Directory\Background\shell\MyReDesk"
$cmdKeyPath = "$shellKeyPath\command"
New-Item -Path $shellKeyPath -Force | Out-Null
Set-ItemProperty -Path $shellKeyPath -Name "(default)" -Value "MyReDesk - Organize & Settings"
Set-ItemProperty -Path $shellKeyPath -Name "Icon" -Value "`"$installedExe`",0"
Set-ItemProperty -Path $shellKeyPath -Name "Position" -Value "Top"

New-Item -Path $cmdKeyPath -Force | Out-Null
Set-ItemProperty -Path $cmdKeyPath -Name "(default)" -Value "`"$installedExe`""
Write-Host "[4/4] Registered Desktop Right-Click Menu -> $installedExe" -ForegroundColor Green

# 7. Launch installed MyReDesk.exe via explorer.exe
if (-not $NoLaunch) {
    explorer.exe $installedExe
    Write-Host "`nMyReDesk is now installed and running!" -ForegroundColor Cyan
}
