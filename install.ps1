# Gal Quote Collector - Installer
# Run this script to install the application

param(
    [switch]$Silent,
    [switch]$DesktopShortcut
)

$ErrorActionPreference = "Stop"
$AppName = "Gal 语录收藏"
$ExeName = "GalQuoteCollector.exe"

# Paths
$InstallDir = "$env:LOCALAPPDATA\Programs\GalQuoteCollector"
$StartMenuDir = "$env:APPDATA\Microsoft\Windows\Start Menu\Programs\$AppName"
$DesktopDir = [Environment]::GetFolderPath("Desktop")
$ScriptDir = Split-Path -Parent $MyInvocation.MyCommand.Definition
$SourceExe = Join-Path $ScriptDir "publish-v105\GalQuoteCollector.exe"

# Admin check
$IsAdmin = ([Security.Principal.WindowsPrincipal][Security.Principal.WindowsIdentity]::GetCurrent()).IsInRole([Security.Principal.WindowsBuiltInRole]::Administrator)

function Write-Info($msg) { Write-Host ">> $msg" -ForegroundColor Cyan }
function Write-Error($msg) { Write-Host "!! $msg" -ForegroundColor Red }

if (!(Test-Path $SourceExe)) {
    Write-Error "Source exe not found: $SourceExe"
    Write-Error "Make sure this script is in the project root with publish-v105\GalQuoteCollector.exe"
    exit 1
}

# 1. Install files
Write-Info "Installing to: $InstallDir"
New-Item -ItemType Directory -Path $InstallDir -Force | Out-Null
Copy-Item $SourceExe "$InstallDir\$ExeName" -Force

# 2. Create Start Menu shortcut
Write-Info "Creating Start Menu shortcut..."
New-Item -ItemType Directory -Path $StartMenuDir -Force | Out-Null
$wshell = New-Object -ComObject WScript.Shell
$shortcut = $wshell.CreateShortcut("$StartMenuDir\$AppName.lnk")
$shortcut.TargetPath = "$InstallDir\$ExeName"
$shortcut.WorkingDirectory = $InstallDir
$shortcut.Description = "Gal Quote Collector"
$shortcut.Save()

# 3. Desktop shortcut (optional)
if ($DesktopShortcut) {
    Write-Info "Creating desktop shortcut..."
    $shortcut = $wshell.CreateShortcut("$DesktopDir\$AppName.lnk")
    $shortcut.TargetPath = "$InstallDir\$ExeName"
    $shortcut.WorkingDirectory = $InstallDir
    $shortcut.Description = "Gal Quote Collector"
    $shortcut.Save()
}

# 4. Uninstall registry entry (per-user, no admin needed)
Write-Info "Registering uninstall..."
$uninstallKey = "HKCU:\Software\Microsoft\Windows\CurrentVersion\Uninstall\GalQuoteCollector"
New-Item -Path $uninstallKey -Force | Out-Null
Set-ItemProperty -Path $uninstallKey -Name "DisplayName" -Value $AppName
Set-ItemProperty -Path $uninstallKey -Name "DisplayVersion" -Value "1.0.0"
Set-ItemProperty -Path $uninstallKey -Name "Publisher" -Value "Liushiweiying"
Set-ItemProperty -Path $uninstallKey -Name "InstallLocation" -Value $InstallDir
Set-ItemProperty -Path $uninstallKey -Name "DisplayIcon" -Value "$InstallDir\$ExeName"
Set-ItemProperty -Path $uninstallKey -Name "UninstallString" -Value "powershell.exe -NoProfile -NoLogo -File `"$InstallDir\uninstall.ps1`""
Set-ItemProperty -Path $uninstallKey -Name "NoModify" -Value 1
Set-ItemProperty -Path $uninstallKey -Name "NoRepair" -Value 1

# Copy uninstall script
@"
# Gal Quote Collector - Uninstaller
`$InstallDir = "$InstallDir"
`$AppName = "$AppName"
`$StartMenuDir = "$StartMenuDir"

# Remove files
if (Test-Path `$InstallDir) { Remove-Item -Recurse -Force `$InstallDir }

# Remove shortcuts
if (Test-Path `$StartMenuDir) { Remove-Item -Recurse -Force `$StartMenuDir }

# Remove desktop shortcut
`$desktop = [Environment]::GetFolderPath("Desktop")
`$desktopLnk = Join-Path `$desktop "`$AppName.lnk"
if (Test-Path `$desktopLnk) { Remove-Item -Force `$desktopLnk }

# Remove uninstall entry
Remove-Item -Path "HKCU:\Software\Microsoft\Windows\CurrentVersion\Uninstall\GalQuoteCollector" -Force

Write-Host "Uninstalled successfully."
Start-Sleep 2
"@ | Out-File -FilePath "$InstallDir\uninstall.ps1" -Encoding UTF8

Write-Info ""
Write-Info "========================" -ForegroundColor Green
Write-Info "Installation complete!" -ForegroundColor Green
Write-Info "========================" -ForegroundColor Green
Write-Info ""
Write-Info "Launch from: Start Menu -> $AppName"
Write-Info "Uninstall:   Settings -> Apps -> $AppName"
Write-Info ""

if (-not $Silent) {
    $reply = Read-Host "Launch now? (Y/n)"
    if ($reply -ne "n") {
        Start-Process "$InstallDir\$ExeName"
    }
}
