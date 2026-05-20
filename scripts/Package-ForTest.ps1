# Empaqueta Odin_Flash para pruebas (Win10/Win11). Copia TODAS las DLL de salida.
# Uso: .\scripts\Package-ForTest.ps1

$ErrorActionPreference = "Stop"
$root = Split-Path -Parent $PSScriptRoot
$msbuild = "${env:ProgramFiles}\Microsoft Visual Studio\2022\Community\MSBuild\Current\Bin\MSBuild.exe"
if (-not (Test-Path $msbuild)) {
    $msbuild = "${env:ProgramFiles(x86)}\Microsoft Visual Studio\2022\BuildTools\MSBuild\Current\Bin\MSBuild.exe"
}
$sln = Join-Path $root "Odin_Flash.sln"
$outDir = Join-Path $root "bin\Release"
$distDir = Join-Path $root "dist\Odin_Flash_Prueba"

Write-Host "Compilando Release..."
& $msbuild $sln /t:Rebuild /p:Configuration=Release /v:minimal
if ($LASTEXITCODE -ne 0) { exit $LASTEXITCODE }

$required = @(
    "Odin_Flash.exe",
    "Odin_Flash.exe.config",
    "MaterialDesignThemes.Wpf.dll",
    "MaterialDesignColors.dll",
    "Microsoft.Xaml.Behaviors.dll"
)
foreach ($f in $required) {
    if (-not (Test-Path (Join-Path $outDir $f))) {
        Write-Error "Falta $f en $outDir"
    }
}

if (Test-Path $distDir) { Remove-Item $distDir -Recurse -Force }
New-Item -ItemType Directory -Path $distDir | Out-Null

Copy-Item (Join-Path $outDir "Odin_Flash.exe") $distDir
Copy-Item (Join-Path $outDir "Odin_Flash.exe.config") $distDir
Get-ChildItem $outDir -Filter "*.dll" | ForEach-Object { Copy-Item $_.FullName $distDir }

$readme = @"
Odin Flash - paquete de prueba
================================

Requisito: .NET Framework 4.8 (Windows 10 u 11).

Ejecutar Odin_Flash.exe desde ESTA carpeta completa.
No borres las DLL (MaterialDesignThemes.Wpf.dll, Microsoft.Xaml.Behaviors.dll, etc.).

Mismo aspecto en Win10 y Win11 (shell semitransparente tipo Ares, sin Acrylic del sistema).
"@
Set-Content -Path (Join-Path $distDir "LEEME.txt") -Value $readme -Encoding UTF8

Write-Host ""
Write-Host "Listo: $distDir"
Get-ChildItem $distDir | Sort-Object Name | Format-Table Name, @{N='KB';E={[math]::Round($_.Length/1KB,1)}} -AutoSize
