<#
.SYNOPSIS
  Builds a single self-contained WidgetSubscription.exe (no .NET runtime required on the target).
.EXAMPLE
  powershell -ExecutionPolicy Bypass -File scripts\publish.ps1
  # -> dist\WidgetSubscription.exe
#>
[CmdletBinding()]
param(
    [string]$Configuration = 'Release',
    [string]$Runtime = 'win-x64',
    [string]$OutDir
)
$ErrorActionPreference = 'Stop'

$scriptDir = Split-Path -Parent $PSCommandPath
$root = (Resolve-Path (Join-Path $scriptDir '..')).Path
if (-not $OutDir) { $OutDir = Join-Path $root 'dist' }
$proj = Join-Path $root 'src\App\WidgetSubscription.App.csproj'

# Prefer the pinned SDK: the on-PATH dotnet on this box is runtime-only.
$sdk = Join-Path $env:USERPROFILE '.dotnet-sdk\dotnet.exe'
if (Test-Path $sdk) {
    $env:DOTNET_ROOT = Join-Path $env:USERPROFILE '.dotnet-sdk'
    $env:DOTNET_MULTILEVEL_LOOKUP = '0'
    $dotnet = $sdk
} else {
    $dotnet = 'dotnet'
}

New-Item -ItemType Directory -Force -Path $OutDir | Out-Null

& $dotnet publish $proj -c $Configuration -r $Runtime --self-contained true `
    -p:PublishSingleFile=true -o $OutDir
if ($LASTEXITCODE -ne 0) { throw "publish failed (exit $LASTEXITCODE)" }

$exe = Join-Path (Resolve-Path $OutDir) 'WidgetSubscription.exe'
Write-Host ""
Write-Host "Built: $exe"
