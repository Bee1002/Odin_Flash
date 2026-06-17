# Empaqueta Odin Flash para distribución (.NET Framework 4.8).
# Uso desde la raíz del proyecto:
#   powershell -ExecutionPolicy Bypass -File tools\Build-Production.ps1
#   powershell -ExecutionPolicy Bypass -File tools\Build-Production.ps1 -Obfuscate -Zip -Installer

param(
    [switch]$Zip,
    [switch]$Installer,
    [switch]$Obfuscate
)

$ErrorActionPreference = 'Stop'
$root = Split-Path $PSScriptRoot -Parent
Set-Location $root

$version = '1.0.1'
$publishPath = Join-Path $root 'publish\release'

$iconScript = Join-Path $root 'tools\Convert-PngToIcon.ps1'
Write-Host '==> Generating icon.ico' -ForegroundColor Cyan
& $iconScript
if ($LASTEXITCODE -ne 0) { throw 'Convert-PngToIcon.ps1 failed.' }

$msbuildCandidates = @(
    "${env:ProgramFiles}\Microsoft Visual Studio\2022\Community\MSBuild\Current\Bin\MSBuild.exe",
    "${env:ProgramFiles}\Microsoft Visual Studio\2022\Professional\MSBuild\Current\Bin\MSBuild.exe",
    "${env:ProgramFiles}\Microsoft Visual Studio\2022\Enterprise\MSBuild\Current\Bin\MSBuild.exe",
    "${env:ProgramFiles(x86)}\Microsoft Visual Studio\2022\BuildTools\MSBuild\Current\Bin\MSBuild.exe"
)
$msbuild = $msbuildCandidates | Where-Object { Test-Path $_ } | Select-Object -First 1
if (-not $msbuild) { throw 'MSBuild not found. Install Visual Studio 2022 Build Tools.' }

$sln = Join-Path $root 'Odin_Flash.sln'
Write-Host "==> MSBuild Release" -ForegroundColor Cyan
& $msbuild $sln /t:Rebuild /p:Configuration=Release /v:minimal
if ($LASTEXITCODE -ne 0) { throw 'MSBuild Release failed.' }

$binRelease = Join-Path $root 'bin\Release'
$exe = Join-Path $binRelease 'Odin_Flash.exe'
if (-not (Test-Path $exe)) { throw "Missing $exe after build." }

@(
    'Odin_Flash.exe.config',
    'icon.ico',
    'MaterialDesignThemes.Wpf.dll',
    'MaterialDesignColors.dll',
    'Microsoft.Xaml.Behaviors.dll',
    'System.Buffers.dll',
    'System.Memory.dll',
    'System.Runtime.CompilerServices.Unsafe.dll'
) | ForEach-Object {
    $p = Join-Path $binRelease $_
    if (-not (Test-Path $p)) { throw "Build incomplete: missing $_" }
}

if (Test-Path $publishPath) { Remove-Item $publishPath -Recurse -Force }
New-Item -ItemType Directory -Force -Path $publishPath | Out-Null

Get-ChildItem $binRelease -File | Where-Object {
    $_.Extension -in '.exe', '.dll', '.config' `
        -and $_.Name -notlike '*.pre-obf.bak' `
        -and $_.Name -ne 'OdinFlash.Protocol.dll.config'
} | ForEach-Object {
    Copy-Item $_.FullName (Join-Path $publishPath $_.Name) -Force
}

$iconSrc = Join-Path $root 'Assets\icon.ico'
if (Test-Path $iconSrc) {
    Copy-Item $iconSrc (Join-Path $publishPath 'icon.ico') -Force
}

Write-Host "OK publish: $publishPath" -ForegroundColor Green

if ($Obfuscate) {
    $obfScript = Join-Path $root 'tools\Obfuscate-Publish.ps1'
    Write-Host '==> Obfuscating Odin_Flash.exe (source code unchanged)' -ForegroundColor Cyan
    & $obfScript -PublishDir $publishPath
    if ($LASTEXITCODE -ne 0) { throw 'Obfuscation failed.' }
}

if ($Zip) {
    if ($Obfuscate) { Start-Sleep -Seconds 2 }
    $zipDir = Join-Path $root 'publish\zip'
    New-Item -ItemType Directory -Force -Path $zipDir | Out-Null
    $zipName = "Odin_Flash_${version}_portable.zip"
    $zipPath = Join-Path $zipDir $zipName
    if (Test-Path $zipPath) { Remove-Item $zipPath -Force }
    Compress-Archive -Path (Join-Path $publishPath '*') -DestinationPath $zipPath -CompressionLevel Optimal
    Write-Host "OK zip: $zipPath" -ForegroundColor Green
}

if ($Installer) {
    $isccCandidates = @(
        "${env:ProgramFiles(x86)}\Inno Setup 6\ISCC.exe",
        "$env:ProgramFiles\Inno Setup 6\ISCC.exe"
    )
    $iscc = $isccCandidates | Where-Object { Test-Path $_ } | Select-Object -First 1
    if (-not $iscc) {
        Write-Host ''
        Write-Host 'Inno Setup 6 not found. Install from: https://jrsoftware.org/isdl.php' -ForegroundColor Yellow
        Write-Host 'Then run: ISCC.exe tools\OdinFlash.iss' -ForegroundColor Yellow
        exit 2
    }

    $iss = Join-Path $root 'tools\OdinFlash.iss'
    Write-Host "==> $iscc $iss" -ForegroundColor Cyan
    & $iscc $iss
    if ($LASTEXITCODE -ne 0) { throw 'Inno Setup compile failed.' }

    $setup = Get-ChildItem (Join-Path $root 'publish\installer') -Filter 'Odin_Flash_*_Setup.exe' |
        Sort-Object LastWriteTime -Descending | Select-Object -First 1
    if ($setup) {
        Write-Host "OK installer: $($setup.FullName)" -ForegroundColor Green
    }
}

Write-Host ''
Write-Host 'Done. Ship publish\release\ or publish\installer\ setup exe.' -ForegroundColor Cyan
Write-Host 'Requires .NET Framework 4.8 on target PC (included in Windows 10/11).' -ForegroundColor DarkGray
