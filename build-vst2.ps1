<#
.SYNOPSIS
  Builds the Renaissance plugins (x64) for use in a DAW: VST2 (e.g. Reason 10) and VST3 (e.g. Reason 14).

.DESCRIPTION
  Produces artifacts\Renaissance-VST2\
      Renaissance.dll                 VST2 instrument
      Renaissance FX.dll              VST2 effect (same binary; the "FX" suffix in the file name selects effect mode)
      Renaissance.vst3\               VST3 bundle (contains both an instrument class and an effect class)
      Renaissance.clap                CLAP plugin (contains both an instrument and an effect class)
      App\                            Renaissance.exe + Host32/Host64, launched by the plugins and embedded in the DAW editor

.PARAMETER Install
  Optional folder to copy everything to, e.g. -Install "C:\VSTPlugins\Renaissance". Add that folder to your DAW's VST2 paths.

.PARAMETER InstallVst3
  Also install the VST3 bundle to the standard VST3 folder (C:\Program Files\Common Files\VST3). Needs an elevated
  (administrator) PowerShell. The App folder stays where it was built or installed; its location is recorded in
  %LOCALAPPDATA%\Renaissance\app-path.txt so the plugin can find it.

.PARAMETER InstallClap
  Also install Renaissance.clap to the standard CLAP folder (C:\Program Files\Common Files\CLAP). Needs an elevated
  (administrator) PowerShell. Like -InstallVst3 it records the App folder in %LOCALAPPDATA%\Renaissance\app-path.txt.
#>
param(
    [string]$Install,
    [switch]$InstallVst3,
    [switch]$InstallClap
)

$ErrorActionPreference = 'Stop'
$root = $PSScriptRoot
$out = Join-Path $root 'artifacts\Renaissance-VST2'

Write-Host '== Publishing native VST2 DLL (NativeAOT) ==' -ForegroundColor Cyan
dotnet publish "$root\src\Renaissance.Vst2" -c Release
if ($LASTEXITCODE) { throw 'VST2 publish failed' }

Write-Host '== Publishing native VST3 module (NativeAOT) ==' -ForegroundColor Cyan
dotnet publish "$root\src\Renaissance.Vst3" -c Release
if ($LASTEXITCODE) { throw 'VST3 publish failed' }

Write-Host '== Publishing native CLAP plugin (NativeAOT) ==' -ForegroundColor Cyan
dotnet publish "$root\src\Renaissance.Clap" -c Release
if ($LASTEXITCODE) { throw 'CLAP publish failed' }

Write-Host '== Building Renaissance app + hosts ==' -ForegroundColor Cyan
dotnet build "$root\src\Renaissance.UI" -c Release
if ($LASTEXITCODE) { throw 'UI build failed' }

New-Item -ItemType Directory -Path $out -Force | Out-Null
if (Test-Path "$out\App") { Remove-Item "$out\App" -Recurse -Force }
New-Item -ItemType Directory -Path "$out\App" | Out-Null
Copy-Item "$root\src\Renaissance.UI\bin\Release\net10.0\*" "$out\App" -Recurse -Force

function Copy-Locked($source, $destination) {
    try { Copy-Item $source $destination -Force -ErrorAction Stop }
    catch { Write-Warning "Could not overwrite '$destination' (is your DAW still running with it loaded?). Close the DAW and run again." }
}

$vst2 = "$root\src\Renaissance.Vst2\bin\Release\net10.0\win-x64\publish\Renaissance.Vst2.dll"
Copy-Locked $vst2 "$out\Renaissance.dll"
Copy-Locked $vst2 "$out\Renaissance FX.dll"

$vst3 = "$root\src\Renaissance.Vst3\bin\Release\net10.0\win-x64\publish\Renaissance.Vst3.dll"
$bundleBin = "$out\Renaissance.vst3\Contents\x86_64-win"
New-Item -ItemType Directory -Path $bundleBin -Force | Out-Null
Copy-Locked $vst3 "$bundleBin\Renaissance.vst3"

$clap = "$root\src\Renaissance.Clap\bin\Release\net10.0\win-x64\publish\Renaissance.Clap.dll"
Copy-Locked $clap "$out\Renaissance.clap"

if ($Install) {
    New-Item -ItemType Directory -Path $Install -Force | Out-Null
    Copy-Item "$out\*" $Install -Recurse -Force
    Write-Host "Installed to $Install" -ForegroundColor Green
}

if ($InstallVst3) {
    $appRoot = if ($Install) { $Install } else { $out }
    $vst3Folder = Join-Path $env:CommonProgramFiles 'VST3'
    New-Item -ItemType Directory -Path $vst3Folder -Force | Out-Null
    Copy-Item "$out\Renaissance.vst3" $vst3Folder -Recurse -Force
    $configDir = Join-Path $env:LOCALAPPDATA 'Renaissance'
    New-Item -ItemType Directory -Path $configDir -Force | Out-Null
    Set-Content -Path (Join-Path $configDir 'app-path.txt') -Value (Join-Path $appRoot 'App\Renaissance.exe')
    Write-Host "VST3 installed to $vst3Folder" -ForegroundColor Green
}

if ($InstallClap) {
    $appRoot = if ($Install) { $Install } else { $out }
    $clapFolder = Join-Path $env:CommonProgramFiles 'CLAP'
    New-Item -ItemType Directory -Path $clapFolder -Force | Out-Null
    Copy-Item "$out\Renaissance.clap" $clapFolder -Force
    $configDir = Join-Path $env:LOCALAPPDATA 'Renaissance'
    New-Item -ItemType Directory -Path $configDir -Force | Out-Null
    Set-Content -Path (Join-Path $configDir 'app-path.txt') -Value (Join-Path $appRoot 'App\Renaissance.exe')
    Write-Host "CLAP installed to $clapFolder" -ForegroundColor Green
}

Write-Host "Done: $out" -ForegroundColor Green
