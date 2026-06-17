# Ofusca Odin_Flash.exe dentro de una carpeta publish ya generada.
# No modifica el código fuente del proyecto.
# Uso: powershell -ExecutionPolicy Bypass -File tools\Obfuscate-Publish.ps1 -PublishDir publish\release

param(
    [Parameter(Mandatory = $true)]
    [string]$PublishDir
)

$ErrorActionPreference = 'Stop'
$root = Split-Path $PSScriptRoot -Parent
$publishPath = if ([IO.Path]::IsPathRooted($PublishDir)) { $PublishDir } else { Join-Path $root $PublishDir }
$exePath = Join-Path $publishPath 'Odin_Flash.exe'

if (-not (Test-Path $exePath)) {
    throw "Missing $exePath. Run Build-Production.ps1 first."
}

$useDotnetTool = Test-Path (Join-Path $root '.config\dotnet-tools.json')
if ($useDotnetTool) {
    Push-Location $root
    try { dotnet tool restore 2>&1 | Out-Null } finally { Pop-Location }
}

$outPath = Join-Path $root 'publish\_obfuscar_staging'
if (Test-Path $outPath) { Remove-Item $outPath -Recurse -Force }
New-Item -ItemType Directory -Force -Path $outPath | Out-Null

$refPath = "${env:ProgramFiles(x86)}\Reference Assemblies\Microsoft\Framework\.NETFramework\v4.8"
if (-not (Test-Path $refPath)) {
    throw "Missing .NET Framework 4.8 reference assemblies: $refPath"
}

$template = Get-Content (Join-Path $PSScriptRoot 'obfuscar.xml') -Raw
$configPath = Join-Path $outPath 'obfuscar.run.xml'
$config = $template `
    -replace 'IN_PATH_PLACEHOLDER', ($publishPath -replace '\\', '/') `
    -replace 'OUT_PATH_PLACEHOLDER', ($outPath -replace '\\', '/') `
    -replace 'REFERENCE_ASSEMBLIES_PLACEHOLDER', ($refPath -replace '\\', '/')
Set-Content -Path $configPath -Value $config -Encoding UTF8

Write-Host "==> Obfuscar: $exePath" -ForegroundColor Cyan
Push-Location $root
try {
    if ($useDotnetTool) {
        & dotnet obfuscar.console $configPath
    } else {
        & obfuscar.console $configPath
    }
} finally {
    Pop-Location
}
if ($LASTEXITCODE -ne 0) { throw "Obfuscar failed (exit $LASTEXITCODE)." }

$obfExe = Join-Path $outPath 'Odin_Flash.exe'
if (-not (Test-Path $obfExe)) { throw "Obfuscar did not produce Odin_Flash.exe" }

$backup = Join-Path $root 'tools\Odin_Flash.exe.pre-obf.bak'
Copy-Item $exePath $backup -Force
Copy-Item $obfExe $exePath -Force

$mapSrc = Join-Path $outPath 'Mapping.txt'
if (Test-Path $mapSrc) {
    $mapDest = Join-Path $root 'tools\obfuscar-mapping-last.txt'
    Copy-Item $mapSrc $mapDest -Force
    Write-Host "Mapping saved (dev only): tools\obfuscar-mapping-last.txt" -ForegroundColor DarkGray
}

Remove-Item $outPath -Recurse -Force
Write-Host "OK obfuscated EXE applied to: $publishPath" -ForegroundColor Green
Write-Host "Test Odin_Flash.exe on real Samsung hardware before shipping." -ForegroundColor Yellow
