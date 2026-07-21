<#
.SYNOPSIS
  Removes the per-user WidgetSubscription install, its Start Menu shortcut, and autostart entry.
.EXAMPLE
  powershell -ExecutionPolicy Bypass -File scripts\uninstall.ps1
#>
[CmdletBinding()]
param()
$ErrorActionPreference = 'Stop'

Get-Process -Name 'WidgetSubscription' -ErrorAction SilentlyContinue | Stop-Process -Force -ErrorAction SilentlyContinue

$installDir = Join-Path $env:LOCALAPPDATA 'WidgetSubscription'
$startMenu = Join-Path $env:APPDATA 'Microsoft\Windows\Start Menu\Programs\Widget Subscription.lnk'
$startup = Join-Path ([Environment]::GetFolderPath('Startup')) 'Widget Subscription.lnk'

Remove-Item $startMenu, $startup -Force -ErrorAction SilentlyContinue
Remove-Item $installDir -Recurse -Force -ErrorAction SilentlyContinue
Write-Host "Uninstalled."
