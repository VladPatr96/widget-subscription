# Builds the Widget Subscription installer (spec §5 / #16).
#
#   1. Publishes the self-contained single-file exe (win-x64, .NET 8 + SkiaSharp bundled).
#   2. Compiles installer\WidgetSubscription.iss with the Inno Setup compiler (ISCC.exe).
#
# Prerequisites: the .NET 8 SDK and Inno Setup 6 (https://jrsoftware.org/isdl.php).
# On this workstation the SDK lives at %USERPROFILE%\.dotnet-sdk; pass -Dotnet to override.
# Output: installer\dist\WidgetSubscription-Setup.exe
[CmdletBinding()]
param(
    [string]$Configuration = 'Release',
    [string]$Dotnet = (Join-Path $env:USERPROFILE '.dotnet-sdk\dotnet.exe')
)

$ErrorActionPreference = 'Stop'
$repo = Split-Path -Parent $PSScriptRoot
$app = Join-Path $repo 'src\App'
$publishDir = Join-Path $app "bin\$Configuration\net8.0\win-x64\publish"

if (-not (Test-Path $Dotnet)) { $Dotnet = 'dotnet' } # fall back to PATH
$env:DOTNET_ROOT = Split-Path -Parent $Dotnet
$env:DOTNET_MULTILEVEL_LOOKUP = '0'

Write-Host '==> Publishing self-contained single-file exe...'
& $Dotnet publish $app -c $Configuration -p:PublishSingleFile=true
if ($LASTEXITCODE -ne 0) { throw "publish failed ($LASTEXITCODE)" }

$exe = Join-Path $publishDir 'WidgetSubscription.exe'
if (-not (Test-Path $exe)) { throw "expected published exe not found: $exe" }
Write-Host "==> Published: $exe ($('{0:N1}' -f ((Get-Item $exe).Length / 1MB)) MB)"

$iscc = @(
    "${env:ProgramFiles(x86)}\Inno Setup 6\ISCC.exe",
    "$env:ProgramFiles\Inno Setup 6\ISCC.exe"
) | Where-Object { Test-Path $_ } | Select-Object -First 1

if (-not $iscc) {
    Write-Warning 'Inno Setup (ISCC.exe) not found. Single-file exe is published; install Inno Setup 6 to build the installer.'
    exit 0
}

Write-Host "==> Compiling installer with $iscc ..."
& $iscc (Join-Path $PSScriptRoot 'WidgetSubscription.iss')
if ($LASTEXITCODE -ne 0) { throw "ISCC failed ($LASTEXITCODE)" }
Write-Host "==> Installer: $(Join-Path $PSScriptRoot 'dist\WidgetSubscription-Setup.exe')"
