# publish.ps1 — publie Echo dans un dossier partage synchronise (SharePoint, OneDrive...).
#
# Structure produite dans -Root (defaut = variable d'environnement ECHO_PUBLISH_ROOT) :
#   <Root>\MAJ\           version.json + Echo-<version>.zip   (source de verite : la version courante)
#   <Root>\Installation\  Installer Echo.cmd + install.ps1 + LISEZMOI.txt  (ce qu'un collegue lance)
#
# L'installeur ET les mises a jour lisent le MEME MAJ\version.json : publier = deposer ici,
# tous les postes se mettent a jour au demarrage d'Echo.
#
# Usage :
#   .\publish.ps1 -Version 5.1.0 -Notes "Corrections vocabulaire"
#   .\publish.ps1                 # republie la version courante (Constants.cs) sans rien changer
#
#   -Version    nouvelle version (met a jour Echo.Core\Constants.cs) ; omis = version actuelle
#   -Root       dossier partage synchronise (defaut : $env:ECHO_PUBLISH_ROOT) ; MAJ\ et Installation\ y sont crees
#   -Notes      note de version affichee cote collegue (version.json)
#   -FfmpegPath ffmpeg.exe a bundler ; defaut = cache essentials, puis PATH (doit avoir libmp3lame)
#   -KeepZips   nombre de zips conserves dans MAJ (defaut 3)
#   -SkipTests  saute la suite de tests (deconseille)
#   -SkipGitHub ne cree pas la release GitHub (par defaut : release v<version> avec le zip + version.json,
#               source des mises a jour de tous les postes ; le code doit etre commite et pousse avant)

param(
    [string]$Version = "",
    [string]$Root = $env:ECHO_PUBLISH_ROOT,
    [string]$Notes = "",
    [string]$FfmpegPath = "",
    [int]$KeepZips = 3,
    [switch]$SkipTests,
    [switch]$SkipGitHub
)

$ErrorActionPreference = "Stop"
$repo = Split-Path -Parent $MyInvocation.MyCommand.Path
$dotnet = Join-Path $env:LOCALAPPDATA "Microsoft\dotnet\dotnet.exe"
if (-not (Test-Path $dotnet)) { $dotnet = "dotnet" }
$majDir = Join-Path $Root "MAJ"
$instDir = Join-Path $Root "Installation"

function Step($m) { Write-Host "`n== $m" -ForegroundColor Cyan }

# garde-fou : ne jamais ecrire au-dessus du dossier de publication
if ([string]::IsNullOrWhiteSpace($Root) -or -not (Test-Path $Root)) { throw "Dossier de publication introuvable : '$Root' (passez -Root ou definissez ECHO_PUBLISH_ROOT)." }

# -- 1. version -----------------------------------------------------------------
$constFile = Join-Path $repo "src\Echo.Core\Constants.cs"
$constText = [System.IO.File]::ReadAllText($constFile)
if ($constText -notmatch 'AppVersion\s*=\s*"([^"]+)"') { throw "AppVersion introuvable dans Constants.cs" }
$current = $Matches[1]
if (-not $Version) { $Version = $current }
if ($Version -ne $current) {
    Step "Version : $current -> $Version (Constants.cs)"
    $constText = $constText -replace '(AppVersion\s*=\s*")[^"]+(")', ('${1}' + $Version + '${2}')
    [System.IO.File]::WriteAllText($constFile, $constText, (New-Object System.Text.UTF8Encoding($false)))
} else {
    Step "Version : $Version (inchangee)"
}

# -- 2. tests ---------------------------------------------------------------------
if (-not $SkipTests) {
    Step "Tests"
    & $dotnet test (Join-Path $repo "tests\Echo.Tests\Echo.Tests.csproj") -c Release --nologo -v q
    if ($LASTEXITCODE -ne 0) { throw "Tests en echec — publication annulee." }
}

# -- 3. publish self-contained ----------------------------------------------------
$stage = Join-Path $repo "dist\_stage"
if (Test-Path $stage) { Remove-Item $stage -Recurse -Force }
Step "dotnet publish (self-contained win-x64)"
& $dotnet publish (Join-Path $repo "src\Echo.App\Echo.App.csproj") -c Release -r win-x64 --self-contained true -o $stage --nologo -v q
if ($LASTEXITCODE -ne 0) { throw "dotnet publish en echec." }

# -- 4. verifications du contenu ---------------------------------------------------
Step "Verification du contenu"
foreach ($f in @("Echo.exe", "ui\index.html", "ui\widget.html", "ui\app.css", "ui\i18n.js", "ui\common.js")) {
    if (-not (Test-Path (Join-Path $stage $f))) { throw "Fichier attendu absent du publish : $f" }
}
if (-not ((Test-Path (Join-Path $stage "Resources\lexicon_default.txt")) -or (Test-Path (Join-Path $stage "lexicon_default.txt")))) {
    throw "lexicon_default.txt absent du publish."
}
# jamais de donnees locales/client dans le livrable
foreach ($banned in @("context_bias.txt", "secret.bin", "secret.txt", "secret_google.bin", "secret_cloudflare.bin", "secret_openrouter.bin", "settings.json")) {
    $hit = Get-ChildItem $stage -Recurse -Filter $banned -ErrorAction SilentlyContinue | Select-Object -First 1
    if ($hit) { throw "SECURITE : $banned trouve dans le publish ($($hit.FullName)) — abandon." }
}
Write-Host "   contenu OK (Echo.exe, ui, lexique ; aucune donnee locale)"
# licence d'Echo + notices des composants tiers (dont ffmpeg, GPL) livrees avec le binaire
foreach ($f in @("LICENSE", "THIRD-PARTY-NOTICES.md")) { Copy-Item (Join-Path $repo $f) (Join-Path $stage $f) -Force }

# -- 5. ffmpeg (essentials de preference) --------------------------------------------
Step "ffmpeg"
if (-not $FfmpegPath) {
    $cache = Join-Path $env:LOCALAPPDATA "Echo-build\ffmpeg\ffmpeg.exe"
    $repoTool = Join-Path $repo "tools\ffmpeg\ffmpeg.exe"
    $cmd = Get-Command ffmpeg -ErrorAction SilentlyContinue
    if (Test-Path $cache) { $FfmpegPath = $cache }
    elseif (Test-Path $repoTool) { $FfmpegPath = $repoTool }
    elseif ($cmd) { $FfmpegPath = $cmd.Source }
    else { throw "ffmpeg introuvable. Placez un build essentials dans $cache (voir tools\get-ffmpeg.ps1) ou passez -FfmpegPath." }
}
$enc = & $FfmpegPath -hide_banner -encoders 2>$null | Select-String "libmp3lame"
if (-not $enc) { throw "Ce ffmpeg n'encode pas le MP3 (libmp3lame absent) : $FfmpegPath" }
New-Item -ItemType Directory -Force (Join-Path $stage "bin") | Out-Null
Copy-Item $FfmpegPath (Join-Path $stage "bin\ffmpeg.exe") -Force
Write-Host ("   bundle : {0} ({1:N0} Mo, libmp3lame OK)" -f $FfmpegPath, ((Get-Item $FfmpegPath).Length / 1MB))

# -- 6. zip + sha256 + version.json  -> MAJ\ ------------------------------------------
Step "Zip + version.json (MAJ)"
New-Item -ItemType Directory -Force $majDir | Out-Null
$zipName = "Echo-$Version.zip"
$zipPath = Join-Path $majDir $zipName
if (Test-Path $zipPath) { Remove-Item $zipPath -Force }
Add-Type -AssemblyName System.IO.Compression.FileSystem
[System.IO.Compression.ZipFile]::CreateFromDirectory($stage, $zipPath, [System.IO.Compression.CompressionLevel]::Optimal, $false)
$sha = (Get-FileHash $zipPath -Algorithm SHA256).Hash.ToLowerInvariant()

$vj = [ordered]@{
    version = $Version
    url     = $zipName          # RELATIF : resolu par Echo/l'installeur contre MAJ\version.json
    sha256  = $sha
    notes   = $Notes
    date    = (Get-Date -Format "yyyy-MM-dd")
}
$vjPath = Join-Path $majDir "version.json"
[System.IO.File]::WriteAllText($vjPath, ($vj | ConvertTo-Json), (New-Object System.Text.UTF8Encoding($false)))
Write-Host ("   MAJ\{0} ({1:N0} Mo), sha256 {2}..." -f $zipName, ((Get-Item $zipPath).Length / 1MB), $sha.Substring(0, 12))

# -- 7. installeur -> Installation\ ---------------------------------------------------
Step "Installation"
New-Item -ItemType Directory -Force $instDir | Out-Null
foreach ($f in @("Installer Echo.cmd", "install.ps1", "LISEZMOI.txt")) {
    Copy-Item (Join-Path $repo "deploy\$f") (Join-Path $instDir $f) -Force
    Write-Host "   Installation\$f"
}

# -- 8. menage : garder les N zips les plus recents dans MAJ ------------------------------
$zips = Get-ChildItem $majDir -Filter "Echo-*.zip" | Sort-Object LastWriteTime -Descending
if ($zips.Count -gt $KeepZips) {
    $zips | Select-Object -Skip $KeepZips | Where-Object { $_.Name -ne $zipName } | ForEach-Object {
        Write-Host "   suppression ancien zip : MAJ\$($_.Name)"
        Remove-Item $_.FullName -Force
    }
}

# -- 9. release GitHub (source des mises a jour) ---------------------------------------
if (-not $SkipGitHub) {
    Step "Release GitHub v$Version"
    $ghRepo = "romainio/echo-recorder"
    $title = "Echo $Version"
    $body = if ($Notes) { $Notes } else { $title }
    & gh release create "v$Version" $zipPath $vjPath --repo $ghRepo --target main --title $title --notes $body
    if ($LASTEXITCODE -ne 0) { throw "Creation de la release GitHub en echec (gh). Le dossier partage est a jour ; relancez avec -SkipTests apres correction." }
    Write-Host "   https://github.com/$ghRepo/releases/tag/v$Version"
}

Remove-Item $stage -Recurse -Force
Step "Publie : version $Version"
Write-Host "  MAJ\          -> $majDir"
Write-Host "  Installation\ -> $instDir"
Write-Host "Les postes ayant synchronise ce dossier verront la mise a jour au prochain demarrage d'Echo."
