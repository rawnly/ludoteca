# Pubblica Ludoteca e crea dist\Ludoteca-Setup-<versione>.exe
# Uso:  powershell -ExecutionPolicy Bypass -File installer\build.ps1
$ErrorActionPreference = 'Stop'
$root = Split-Path $PSScriptRoot -Parent

$iscc = @(
    "$env:LOCALAPPDATA\Programs\Inno Setup 6\ISCC.exe",
    "${env:ProgramFiles(x86)}\Inno Setup 6\ISCC.exe",
    "$env:ProgramFiles\Inno Setup 6\ISCC.exe"
) | Where-Object { Test-Path $_ } | Select-Object -First 1
if (-not $iscc) { throw "Inno Setup 6 non trovato. Installa con: winget install JRSoftware.InnoSetup" }

$publish = Join-Path $root 'publish'
if (Test-Path $publish) { Remove-Item $publish -Recurse -Force }

dotnet publish (Join-Path $root 'Ludoteca.csproj') -c Release -r win-x64 --self-contained true -o $publish
if ($LASTEXITCODE -ne 0) { throw "dotnet publish fallito" }

& $iscc /Q (Join-Path $PSScriptRoot 'Ludoteca.iss')
if ($LASTEXITCODE -ne 0) { throw "ISCC fallito" }

Get-ChildItem (Join-Path $root 'dist') -Filter 'Ludoteca-Setup-*.exe' |
    Sort-Object LastWriteTime -Descending | Select-Object -First 1 |
    ForEach-Object { "Installer pronto: $($_.FullName) ({0:N0} MB)" -f ($_.Length / 1MB) }
