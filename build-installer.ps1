<#
.SYNOPSIS
  Builds the Renaissance Windows installer (.msi).

.DESCRIPTION
  1. Publishes the native VST2 and VST3 plugins (NativeAOT).
  2. Publishes the Renaissance app self-contained (no .NET install needed) together with the 32/64-bit plugin hosts.
  3. Packages everything with WiX into artifacts\installer\Renaissance-<version>-x64.msi

  Installs:
    %LOCALAPPDATA%\Renaissance\App                       the app and plugin hosts
    C:\Program Files\Common Files\VST2                   Renaissance.dll, Renaissance FX.dll
    C:\Program Files\Common Files\VST3\Renaissance.vst3  VST3 bundle
  Installing needs administrator rights (Program Files). Close your DAW before installing or upgrading.
#>
$ErrorActionPreference = 'Stop'
$root = $PSScriptRoot
$stage = Join-Path $root 'artifacts\stage'

$version = (Get-Content "$root\version.txt" -Raw).Trim()
Write-Host "Renaissance $version" -ForegroundColor Cyan

if (Test-Path $stage) { Remove-Item $stage -Recurse -Force }
New-Item -ItemType Directory -Path "$stage\Vst2", "$stage\Vst3", "$stage\App" | Out-Null

Write-Host '== VST2 plugin (NativeAOT) ==' -ForegroundColor Cyan
dotnet publish "$root\src\Renaissance.Vst2" -c Release
if ($LASTEXITCODE) { throw 'VST2 publish failed' }
$vst2 = "$root\src\Renaissance.Vst2\bin\Release\net10.0\win-x64\publish\Renaissance.Vst2.dll"
Copy-Item $vst2 "$stage\Vst2\Renaissance.dll"
Copy-Item $vst2 "$stage\Vst2\Renaissance FX.dll"

Write-Host '== VST3 plugin (NativeAOT) ==' -ForegroundColor Cyan
dotnet publish "$root\src\Renaissance.Vst3" -c Release
if ($LASTEXITCODE) { throw 'VST3 publish failed' }
Copy-Item "$root\src\Renaissance.Vst3\bin\Release\net10.0\win-x64\publish\Renaissance.Vst3.dll" "$stage\Vst3\Renaissance.vst3"

Write-Host '== App + hosts (self-contained) ==' -ForegroundColor Cyan
dotnet publish "$root\src\Renaissance.UI" -c Release -r win-x64 --self-contained true -o "$stage\App"
if ($LASTEXITCODE) { throw 'App publish failed' }
foreach ($required in 'Renaissance.exe', 'Hosts\Host32\Renaissance.Host32.exe', 'Hosts\Host64\Renaissance.Host64.exe') {
    if (-not (Test-Path "$stage\App\$required")) { throw "Missing $required in the published app" }
}

Write-Host '== MSI (WiX) ==' -ForegroundColor Cyan
dotnet build "$root\installer\Renaissance.Installer.wixproj" -c Release "-p:ProductVersion=$version" "-p:StageDir=$stage"
if ($LASTEXITCODE) { throw 'MSI build failed' }

$msi = Get-ChildItem "$root\artifacts\installer" -Filter *.msi | Sort-Object LastWriteTime -Descending | Select-Object -First 1
Write-Host ("MSI: {0} ({1} MB)" -f $msi.FullName, [math]::Round($msi.Length / 1MB)) -ForegroundColor Green
