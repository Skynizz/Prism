<#
    Publie une nouvelle version de Prism sur GitHub.

    Exemple :
        .\scripts\release.ps1 -Version 1.1.0 -Notes "Nouveautes de la version"
        .\scripts\release.ps1 -Version 1.2.0-beta.1 -Prerelease -NotesFile notes.md
        .\scripts\release.ps1 -Version 1.1.0 -CertThumbprint ABCDEF... (signature de code)
        .\scripts\release.ps1 -Version 1.1.0 -NoPublish   (tout construire, sans publier)

    Produit, dans artifacts\ :
      - Prism-Setup-<version>.exe       installeur (Inno Setup), par utilisateur, sans admin
      - Prism-<version>-win-x64.zip     version portable, lue par les mises a jour automatiques
      - un fichier .sha256 pour chacun
    GitHub ajoute de lui-meme l'archive du code source a chaque release.
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
$dist = Join-Path $root 'artifacts'
$numeric = ($Version -split '-')[0]
if ($numeric -notmatch '^\d+\.\d+\.\d+$') { throw "Version attendue : 1.2.3 ou 1.2.3-beta.1 (recu : $Version)" }

# Etiquette de preversion : "1.2.0-dev.1" -> "dev". Elle pilote tout le canal : dossier de
# donnees de l'application, identite de l'installeur, et publication en preversion.
$label = if ($Version -ne $numeric) { (($Version -split '-', 2)[1] -split '\.')[0] } else { '' }
$isPre = $Prerelease -or $label -ne ''
if ($label) { Write-Host "Canal de test : $label (donnees dans %LOCALAPPDATA%\Prism-$label)" }

$dotnet = Join-Path $env:LOCALAPPDATA 'Microsoft\dotnet\dotnet.exe'
if (-not (Test-Path $dotnet)) { $dotnet = 'dotnet' }

function Sign-File([string]$path) {
    if (-not $CertThumbprint) { return }
    $signtool = Get-ChildItem "${env:ProgramFiles(x86)}\Windows Kits\10\bin" -Recurse -Filter signtool.exe -ErrorAction SilentlyContinue |
        Where-Object FullName -match '\\x64\\' | Sort-Object FullName -Descending | Select-Object -First 1
    if (-not $signtool) { throw "signtool.exe introuvable (Windows SDK)" }
    & $signtool.FullName sign /sha1 $CertThumbprint /fd SHA256 /tr http://timestamp.digicert.com /td SHA256 $path
    if ($LASTEXITCODE -ne 0) { throw "Signature echouee : $path" }
}

function Write-Hash([string]$path) {
    $hash = (Get-FileHash $path -Algorithm SHA256).Hash.ToLowerInvariant()
    $name = Split-Path $path -Leaf
    [IO.File]::WriteAllText("$path.sha256", "$hash  $name`n", (New-Object Text.UTF8Encoding($false)))
    Write-Host "$name  SHA-256 $hash"
    return "$path.sha256"
}

# 1. Version unique, lue par Prism pour se comparer aux releases.
#    Lecture explicite en UTF-8 : PowerShell 5.1 lirait le projet en ANSI et abimerait « © ».
$xml = [IO.File]::ReadAllText($project, [Text.Encoding]::UTF8)
$xml = [regex]::Replace($xml, '<Version>[^<]*</Version>', "<Version>$numeric</Version>")
[IO.File]::WriteAllText($project, $xml, (New-Object Text.UTF8Encoding($false)))
Write-Host "Version $Version dans Prism.csproj"

# 2. Publication autonome : un seul Prism.exe, runtime .NET inclus.
$out = Join-Path $dist "publish-$Version"
if (Test-Path $out) { Remove-Item $out -Recurse -Force }
# InformationalVersion porte l'etiquette : c'est elle que l'application lit pour savoir
# dans quel dossier de donnees ecrire (Services\AppPaths.cs).
& $dotnet publish $project -c Release -r win-x64 --self-contained true `
    -p:PublishSingleFile=true -p:IncludeNativeLibrariesForSelfExtract=true `
    -p:DebugType=none -p:Version=$numeric -p:InformationalVersion=$Version -o $out
if ($LASTEXITCODE -ne 0) { throw "Publication echouee" }

# 3. Signature de code (recommandee : sans elle, SmartScreen et Smart App Control avertissent).
if ($CertThumbprint) { Sign-File (Join-Path $out 'Prism.exe') }
else { Write-Warning "Aucun certificat : les binaires ne sont pas signes." }

# 4. Version portable et son empreinte (lues par les mises a jour automatiques).
$zip = Join-Path $dist "Prism-$Version-win-x64.zip"
if (Test-Path $zip) { Remove-Item $zip -Force }
Compress-Archive -Path (Join-Path $out '*') -DestinationPath $zip
$assets = @($zip, (Write-Hash $zip))

# 5. Installeur Inno Setup.
$iscc = Join-Path $env:LOCALAPPDATA 'Programs\Inno Setup 6\ISCC.exe'
if (-not (Test-Path $iscc)) { $iscc = (Get-Command ISCC.exe -ErrorAction SilentlyContinue).Source }
if ($iscc) {
    & $iscc /Q "/DAppVersion=$numeric" "/DFullVersion=$Version" "/DChannel=$label" `
        "/DPublishDir=$out" "/DOutputDir=$dist" (Join-Path $root 'installer\Prism.iss')
    if ($LASTEXITCODE -ne 0) { throw "Compilation de l'installeur echouee" }
    $setup = Join-Path $dist "Prism-Setup-$Version.exe"
    Sign-File $setup
    $assets += @($setup, (Write-Hash $setup))
} else {
    Write-Warning "Inno Setup introuvable (winget install JRSoftware.InnoSetup --scope user) : pas d'installeur."
}

if ($NoPublish) { Write-Host "Publication GitHub ignoree (-NoPublish)."; return }

# 6. Release GitHub : c'est elle que les Prism installes lisent.
$ghArgs = @('release', 'create', "v$Version") + $assets + @('--repo', $Repo, '--title', "Prism $Version")
if ($NotesFile) { $ghArgs += @('--notes-file', $NotesFile) } else { $ghArgs += @('--notes', $Notes) }
if ($isPre) { $ghArgs += '--prerelease' }
& gh @ghArgs
if ($LASTEXITCODE -ne 0) { throw "Creation de la release echouee" }
Write-Host "Release v$Version publiee sur $Repo"
