# Crea una release di Ludoteca con Velopack e, con -Publish, la carica nelle GitHub Releases.
#
#   .\release.ps1                      prepara tutto in .\releases senza pubblicare niente
#   .\release.ps1 -Publish             prepara e pubblica (tag v<versione> sul commit attuale)
#   .\release.ps1 -Publish -Notes note.md
#
# La versione è <Version> in Ludoteca.csproj: va alzata prima di ogni nuova release.
# Servono: .NET 8 SDK, vpk (dotnet tool install -g vpk), gh autenticato (gh auth login).
param(
    [switch]$Publish,
    [string]$Notes
)
$ErrorActionPreference = 'Stop'
$root = $PSScriptRoot
$repo = 'https://github.com/rawnly/ludoteca'
$publishDir = Join-Path $root 'publish'
$releasesDir = Join-Path $root 'releases'

[xml]$project = Get-Content (Join-Path $root 'Ludoteca.csproj')
$version = @($project.Project.PropertyGroup.Version | Where-Object { $_ })[0]
if (-not $version) { throw 'Version non trovata in Ludoteca.csproj' }
Write-Host "Ludoteca $version" -ForegroundColor Cyan

if ($Publish) {
    # La release deve corrispondere esattamente a un commit.
    if (git -C $root status --porcelain) { throw 'Ci sono modifiche non committate: fai commit prima di pubblicare.' }
    if (git -C $root tag --list "v$version") { throw "Il tag v$version esiste già: alza <Version> in Ludoteca.csproj." }
}

$token = (gh auth token 2>$null)
if (-not $token) { if ($Publish) { throw 'gh non è autenticato: esegui gh auth login.' } }

# 1) Pubblica l'app (con .NET incluso).
if (Test-Path $publishDir) { Remove-Item $publishDir -Recurse -Force }
dotnet publish (Join-Path $root 'Ludoteca.csproj') -c Release -r win-x64 --self-contained true -o $publishDir
if ($LASTEXITCODE -ne 0) { throw 'dotnet publish fallito' }

# 2) Scarica l'ultima release pubblicata: serve a vpk per creare il pacchetto "delta"
#    (gli utenti scaricano solo le differenze). Alla prima release non c'è niente: va bene.
$downloadArgs = @('download', 'github', '--repoUrl', $repo, '-o', $releasesDir)
if ($token) { $downloadArgs += @('--token', $token) }
vpk @downloadArgs
if ($LASTEXITCODE -ne 0) { Write-Host 'Nessuna release precedente scaricata (normale alla prima release).' -ForegroundColor Yellow }

# 3) Crea Setup.exe, lo zip portatile e i pacchetti di aggiornamento.
$packArgs = @(
    'pack',
    '--packId', 'Ludoteca',
    '--packVersion', $version,
    '--packDir', $publishDir,
    '--mainExe', 'Ludoteca.exe',
    '--packTitle', 'Ludoteca',
    '--packAuthors', 'Federico Vitale',
    '--icon', (Join-Path $root 'Assets\Ludoteca.ico'),
    '-o', $releasesDir
)
if ($Notes) { $packArgs += @('--releaseNotes', (Resolve-Path $Notes)) }
vpk @packArgs
if ($LASTEXITCODE -ne 0) { throw 'vpk pack fallito' }

Get-ChildItem $releasesDir -File | Where-Object { $_.Name -like "*$version*" -or $_.Name -like 'Ludoteca-win-*' } |
    ForEach-Object { '  {0,-45} {1,8:N1} MB' -f $_.Name, ($_.Length / 1MB) }

# 4) Pubblica su GitHub.
if ($Publish) {
    vpk upload github --repoUrl $repo --token $token -o $releasesDir `
        --publish --releaseName "Ludoteca $version" --tag "v$version" --targetCommitish (git -C $root rev-parse HEAD)
    if ($LASTEXITCODE -ne 0) { throw 'vpk upload fallito' }
    git -C $root fetch --tags --quiet
    Write-Host "Pubblicata: $repo/releases/tag/v$version" -ForegroundColor Green
} else {
    Write-Host "Pronta in $releasesDir (non pubblicata). Per pubblicarla: .\release.ps1 -Publish" -ForegroundColor Green
}
