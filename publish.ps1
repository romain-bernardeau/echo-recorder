# publish.ps1 — publie Echo dans un dossier partage synchronise (SharePoint, OneDrive...).
#
# Structure produite dans -Root (defaut = variable d'environnement ECHO_PUBLISH_ROOT) :
#   <Root>\MAJ\           version.json + Echo-<version>.zip   (source de verite : la version courante)
#   <Root>\Installation\  Echo-Setup.exe + LISEZMOI.txt  (ce qu'un collegue lance)
#
# Echo-Setup.exe (Inno Setup, deploy\Echo.iss) installe la version publiee ; ensuite chaque poste se
# met a jour au demarrage d'Echo depuis la release GitHub (zip + version.json + Echo-Setup.exe).
# Prerequis : Inno Setup 6 (winget install JRSoftware.InnoSetup --scope user).
#
# Usage :
#   .\publish.ps1 -Version 5.1.0 -Notes "Corrections vocabulaire"
#   .\publish.ps1                 # republie la version courante (Constants.cs) sans rien changer
#
#   -Version    nouvelle version (met a jour Echo.Core\Constants.cs) ; omis = version actuelle
#   -Root       dossier partage synchronise (defaut : $env:ECHO_PUBLISH_ROOT) ; MAJ\ et Installation\ y sont crees
#   -Notes      note de version affichee cote collegue (version.json)
#   -FfmpegPath ffmpeg.exe a bundler (avec ses licences a cote) ; defaut = %LOCALAPPDATA%\Echo-build\ffmpeg-audio (tools\get-ffmpeg.ps1)
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
# licence d'Echo + notices des composants tiers (dont ffmpeg, LGPL) livrees avec le binaire
foreach ($f in @("LICENSE", "THIRD-PARTY-NOTICES.md")) { Copy-Item (Join-Path $repo $f) (Join-Path $stage $f) -Force }

# -- 5. ffmpeg audio LGPL (tools\get-ffmpeg.ps1) + ses licences ----------------------------
Step "ffmpeg"
$ffDir = if ($FfmpegPath) { Split-Path -Parent $FfmpegPath } else { Join-Path $env:LOCALAPPDATA "Echo-build\ffmpeg-audio" }
$FfmpegPath = Join-Path $ffDir "ffmpeg.exe"
if (-not (Test-Path $FfmpegPath)) { throw "ffmpeg introuvable : $FfmpegPath. Lancez tools\get-ffmpeg.ps1." }
$ver = (& $FfmpegPath -hide_banner -version 2>$null) -join "`n"
if ($ver -match '--enable-(gpl|nonfree)') { throw "Ce ffmpeg contient des composants GPL ou nonfree : seul le build audio LGPL (tools\get-ffmpeg.ps1) est livre." }
$enc = & $FfmpegPath -hide_banner -encoders 2>$null | Select-String "libmp3lame"
if (-not $enc) { throw "Ce ffmpeg n'encode pas le MP3 (libmp3lame absent) : $FfmpegPath" }
New-Item -ItemType Directory -Force (Join-Path $stage "bin") | Out-Null
Copy-Item $FfmpegPath (Join-Path $stage "bin\ffmpeg.exe") -Force
# licence LGPL, notice FFmpeg, licence LAME et provenance (sources : jointes a chaque release GitHub)
foreach ($lic in @("FFMPEG-COPYING.LGPLv2.1.txt", "FFMPEG-LICENSE.md", "LAME-COPYING.txt", "BUILD-INFO.txt")) {
    $p = Join-Path $ffDir $lic
    if (-not (Test-Path $p)) { throw "$lic absent a cote de ffmpeg.exe ($ffDir) : la licence doit accompagner le binaire." }
    Copy-Item $p (Join-Path $stage "bin\$lic") -Force
}
Write-Host ("   bundle : {0} ({1:N1} Mo, LGPL, libmp3lame OK, licences jointes)" -f $FfmpegPath, ((Get-Item $FfmpegPath).Length / 1MB))
if (-not $SkipGitHub -and @(Get-ChildItem (Join-Path $env:LOCALAPPDATA "Echo-build\ffmpeg-audio-dist") -File -ErrorAction SilentlyContinue).Count -lt 5) {
    throw "Sources ffmpeg absentes (Echo-build\ffmpeg-audio-dist) : lancez tools\get-ffmpeg.ps1, elles sont jointes a la release (LGPL)."
}

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

# -- 7. installeur Echo-Setup.exe (Inno Setup) -> Installation\ ----------------------------
Step "Installeur Echo-Setup.exe"
$iscc = @((Join-Path $env:LOCALAPPDATA "Programs\Inno Setup 6\ISCC.exe"),
          (Join-Path ${env:ProgramFiles(x86)} "Inno Setup 6\ISCC.exe")) | Where-Object { Test-Path $_ } | Select-Object -First 1
if (-not $iscc) { $iscc = (Get-Command ISCC -ErrorAction SilentlyContinue).Source }
if (-not $iscc) { throw "Inno Setup 6 introuvable (ISCC.exe). Installez-le : winget install JRSoftware.InnoSetup --scope user" }
$setupOut = Join-Path $repo "dist"
& $iscc /Q "/DAppVer=$Version" "/DStageDir=$stage" "/DOutDir=$setupOut" (Join-Path $repo "deploy\Echo.iss")
if ($LASTEXITCODE -ne 0) { throw "Compilation de l'installeur en echec (ISCC $LASTEXITCODE)." }
$setupPath = Join-Path $setupOut "Echo-Setup.exe"
Write-Host ("   Echo-Setup.exe ({0:N0} Mo)" -f ((Get-Item $setupPath).Length / 1MB))
New-Item -ItemType Directory -Force $instDir | Out-Null
Copy-Item $setupPath (Join-Path $instDir "Echo-Setup.exe") -Force
Copy-Item (Join-Path $repo "deploy\LISEZMOI.txt") (Join-Path $instDir "LISEZMOI.txt") -Force
Write-Host "   Installation\Echo-Setup.exe + LISEZMOI.txt"
# l'ancien installeur (script) est remplace par Echo-Setup.exe
foreach ($old in @("Installer Echo.cmd", "install.ps1")) {
    $p = Join-Path $instDir $old
    if (Test-Path $p) { Remove-Item $p -Force; Write-Host "   suppression ancien installeur : Installation\$old" }
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
    # ffmpeg (LGPL) : binaire + sources exactes joints a chaque release (tools\get-ffmpeg.ps1 les met en cache)
    $ffDist = Join-Path $env:LOCALAPPDATA "Echo-build\ffmpeg-audio-dist"
    $ffAssets = @(Get-ChildItem $ffDist -File -ErrorAction SilentlyContinue | ForEach-Object FullName)
    if ($ffAssets.Count -lt 5) { throw "Sources ffmpeg absentes de $ffDist : lancez tools\get-ffmpeg.ps1 (obligation LGPL)." }
    $body += "`n`nFFmpeg (LGPL, audio only): ffmpeg-audio-*-win64.zip, complete source (ffmpeg, LAME, build.sh) and SHA256SUMS.txt are attached."
    & gh release create "v$Version" $zipPath $vjPath $setupPath @ffAssets --repo $ghRepo --target main --title $title --notes $body
    if ($LASTEXITCODE -ne 0) { throw "Creation de la release GitHub en echec (gh). Le dossier partage est a jour ; relancez avec -SkipTests apres correction." }
    Write-Host "   https://github.com/$ghRepo/releases/tag/v$Version"
}

Remove-Item $stage -Recurse -Force
Step "Publie : version $Version"
Write-Host "  MAJ\          -> $majDir"
Write-Host "  Installation\ -> $instDir"
Write-Host "Les postes ayant synchronise ce dossier verront la mise a jour au prochain demarrage d'Echo."
