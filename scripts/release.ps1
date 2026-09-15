<#
    Publie une nouvelle version de Prism sur GitHub.

    Exemple :
        .\scripts\release.ps1 -Version 1.1.0 -Notes "Nouveautes de la version"
        .\scripts\release.ps1 -Version 1.2.0-beta.1 -Prerelease -NotesFile notes.md
        .\scripts\release.ps1 -Version 1.1.0 -CertThumbprint ABCDEF... (signature de code)

    Etapes : version dans Prism.csproj, publication autonome win-x64 (aucun runtime a
    installer chez l'utilisateur), signature si un certificat est fourni, archive
    Prism-<version>-win-x64.zip et son empreinte .sha256, puis release GitHub (gh).
    Prism installe verifie l'empreinte — et la signature s'il est lui-meme signe.
#>
param(
    [Parameter(Mandatory)] [string] $Version,
    [switch] $Prerelease,
    [string] $Notes = "",
    [string] $NotesFile,
    [string] $Repo = "Skynizz/Prism",
    [string] $CertThumbprint,
    [switch] $NoPublish
)

$ErrorActionPreference = 'Stop'
$root = Split-Path $PSScriptRoot -Parent
$project = Join-Path $root 'Prism.csproj'
$numeric = ($Version -split '-')[0]
if ($numeric -notmatch '^\d+\.\d+\.\d+$') { throw "Version attendue : 1.2.3 ou 1.2.3-beta.1 (recu : $Version)" }

$dotnet = Join-Path $env:LOCALAPPDATA 'Microsoft\dotnet\dotnet.exe'
if (-not (Test-Path $dotnet)) { $dotnet = 'dotnet' }

# 1. Version unique, lue par Prism pour se comparer aux releases.
# Lecture explicite en UTF-8 : PowerShell 5.1 lirait le projet en ANSI et abimerait « © ».
$xml = [IO.File]::ReadAllText($project, [Text.Encoding]::UTF8)
$xml = [regex]::Replace($xml, '<Version>[^<]*</Version>', "<Version>$numeric</Version>")
[IO.File]::WriteAllText($project, $xml, (New-Object Text.UTF8Encoding($false)))
Write-Host "Version $Version dans Prism.csproj"

# 2. Publication autonome : un seul Prism.exe, runtime .NET inclus.
$out = Join-Path $root "artifacts\publish-$Version"
if (Test-Path $out) { Remove-Item $out -Recurse -Force }
& $dotnet publish $project -c Release -r win-x64 --self-contained true `
    -p:PublishSingleFile=true -p:IncludeNativeLibrariesForSelfExtract=true `
    -p:DebugType=none -o $out
if ($LASTEXITCODE -ne 0) { throw "Publication echouee" }

# 3. Signature de code (recommandee : sans elle, SmartScreen et Smart App Control avertissent).
if ($CertThumbprint) {
    $signtool = Get-ChildItem "${env:ProgramFiles(x86)}\Windows Kits\10\bin" -Recurse -Filter signtool.exe -ErrorAction SilentlyContinue |
        Where-Object FullName -match '\\x64\\' | Sort-Object FullName -Descending | Select-Object -First 1
    if (-not $signtool) { throw "signtool.exe introuvable (Windows SDK)" }
    & $signtool.FullName sign /sha1 $CertThumbprint /fd SHA256 /tr http://timestamp.digicert.com /td SHA256 (Join-Path $out 'Prism.exe')
    if ($LASTEXITCODE -ne 0) { throw "Signature echouee" }
} else {
    Write-Warning "Aucun certificat : Prism.exe n'est pas signe."
}

# 4. Archive et empreinte : Prism refuse toute archive dont l'empreinte ne correspond pas.
$dist = Join-Path $root 'artifacts'
$zipName = "Prism-$numeric-win-x64.zip"
$zip = Join-Path $dist $zipName
if (Test-Path $zip) { Remove-Item $zip -Force }
Compress-Archive -Path (Join-Path $out '*') -DestinationPath $zip
$hash = (Get-FileHash $zip -Algorithm SHA256).Hash.ToLowerInvariant()
$hashFile = "$zip.sha256"
[IO.File]::WriteAllText($hashFile, "$hash  $zipName`n", (New-Object Text.UTF8Encoding($false)))
Write-Host "$zipName  SHA-256 $hash"

if ($NoPublish) { Write-Host "Publication GitHub ignoree (-NoPublish)."; return }

# 5. Release GitHub : c'est elle que les Prism installes lisent.
$args = @('release', 'create', "v$Version", $zip, $hashFile, '--repo', $Repo, '--title', "Prism $Version")
if ($NotesFile) { $args += @('--notes-file', $NotesFile) } else { $args += @('--notes', $Notes) }
if ($Prerelease) { $args += '--prerelease' }
& gh @args
if ($LASTEXITCODE -ne 0) { throw "Creation de la release echouee" }
Write-Host "Release v$Version publiee sur $Repo"
