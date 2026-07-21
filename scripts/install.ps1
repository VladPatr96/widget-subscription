<#
.SYNOPSIS
  Installs WidgetSubscription.exe to %LOCALAPPDATA%\WidgetSubscription, adds a Start Menu
  shortcut, and (optionally) an autostart entry. No admin rights needed (per-user install).
.EXAMPLE
  powershell -ExecutionPolicy Bypass -File scripts\install.ps1 -Autostart -Launch
#>
[CmdletBinding()]
param(
    [switch]$Autostart,
    [switch]$Launch,
    [string]$Source
)
$ErrorActionPreference = 'Stop'

$scriptDir = Split-Path -Parent $PSCommandPath
if (-not $Source) { $Source = Join-Path $scriptDir '..\dist\WidgetSubscription.exe' }

if (-not (Test-Path $Source)) {
    throw "Not found: $Source`nRun scripts\publish.ps1 first."
}

$installDir = Join-Path $env:LOCALAPPDATA 'WidgetSubscription'
New-Item -ItemType Directory -Force -Path $installDir | Out-Null
$target = Join-Path $installDir 'WidgetSubscription.exe'

# Release a running instance so the copy is not file-locked.
Get-Process -Name 'WidgetSubscription' -ErrorAction SilentlyContinue | Stop-Process -Force -ErrorAction SilentlyContinue
Start-Sleep -Milliseconds 300
Copy-Item -Path $Source -Destination $target -Force
Write-Host "Installed: $target"

$shell = New-Object -ComObject WScript.Shell
function New-Shortcut([string]$LinkPath, [string]$TargetExe) {
    $sc = $shell.CreateShortcut($LinkPath)
    $sc.TargetPath = $TargetExe
    $sc.WorkingDirectory = Split-Path $TargetExe
    $sc.Description = 'Widget Subscription - Claude Code limits'
    $sc.Save()
}

$startMenu = Join-Path $env:APPDATA 'Microsoft\Windows\Start Menu\Programs'
New-Shortcut (Join-Path $startMenu 'Widget Subscription.lnk') $target
Write-Host "Start Menu shortcut created."

if ($Autostart) {
    $startup = [Environment]::GetFolderPath('Startup')
    New-Shortcut (Join-Path $startup 'Widget Subscription.lnk') $target
    Write-Host "Autostart enabled (runs at login)."
}

if ($Launch) {
    Start-Process $target
    Write-Host "Launched."
}
