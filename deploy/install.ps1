# install.ps1 — installe (ou reinstalle) Echo depuis un dossier partage synchronise (SharePoint, OneDrive...).
# Lance par "Installer Echo.cmd" (double-clic), depuis le dossier Installation\. Sans droits admin.
#
# Structure attendue (a cote de ce script) :
#   Installation\  <- ce script
#   MAJ\           <- version.json + Echo-<version>.zip   (source de verite de la version)
#
# Ce que fait ce script :
#   1. lit ..\MAJ\version.json et extrait le zip correspondant vers %LOCALAPPDATA%\Programs\Echo ;
#   2. regle l'URL de mise a jour d'Echo sur ..\MAJ\version.json (les futures versions
#      s'installeront alors toutes seules via Echo) ;
#   3. cree un raccourci "Echo" au menu Demarrer et lance l'application.
#
# -DevTest -TargetDir X : mode test (extraction seule vers X, aucun reglage/raccourci/lancement).

param(
    [switch]$DevTest,
    [string]$TargetDir = ""
)

$ErrorActionPreference = "Stop"
if (-not $TargetDir) { $TargetDir = Join-Path $env:LOCALAPPDATA "Programs\Echo" }

Write-Host ""
Write-Host "=== Installation d'Echo ===" -ForegroundColor Cyan

# -- localiser MAJ\version.json (a cote de Installation\) ------------------------------
$majDir = Join-Path (Split-Path $PSScriptRoot -Parent) "MAJ"
$vjPath = Join-Path $majDir "version.json"
if (-not (Test-Path $vjPath)) {
    throw "MAJ\version.json introuvable. Lancez 'Installer Echo.cmd' depuis le dossier Installation\ de la bibliotheque Echo synchronisee (le dossier MAJ\ doit etre a cote)."
}
$vj = [System.IO.File]::ReadAllText($vjPath) | ConvertFrom-Json   # UTF-8 explicite (cf. plus bas)
$zipPath = $vj.url
if (-not [System.IO.Path]::IsPathRooted($zipPath)) { $zipPath = Join-Path $majDir $zipPath }
if (-not (Test-Path $zipPath)) { throw "Archive introuvable : $zipPath (attendez la fin de la synchronisation OneDrive puis relancez)." }
Write-Host ("Version {0} : {1}" -f $vj.version, (Split-Path $zipPath -Leaf))

# -- verification d'integrite (meme controle que l'updater d'Echo : sha256 OBLIGATOIRE) --
if (-not $vj.sha256) { throw "version.json sans sha256 : installation refusee (publication incomplete ?)." }
$actual = (Get-FileHash $zipPath -Algorithm SHA256).Hash.ToLowerInvariant()
if ($actual -ne $vj.sha256.ToLowerInvariant()) { throw "Archive corrompue (sha256 different) : resynchronisez le dossier puis relancez." }
Write-Host "Integrite OK (sha256)"

# -- fermer Echo s'il tourne ----------------------------------------------------------
if (-not $DevTest) {
    $p = Get-Process -Name "Echo" -ErrorAction SilentlyContinue
    if ($p) {
        Write-Host "Echo est ouvert : fermeture pour la mise a jour..." -ForegroundColor Yellow
        try { Stop-Process -Name "Echo" -Force -ErrorAction Stop } catch {}
        Start-Sleep -Seconds 2
    }
}

# -- extraire + copier -----------------------------------------------------------------
$tmp = Join-Path $env:TEMP ("echo_install_" + [Guid]::NewGuid().ToString("N"))
New-Item -ItemType Directory -Force $tmp | Out-Null
Add-Type -AssemblyName System.IO.Compression.FileSystem
[System.IO.Compression.ZipFile]::ExtractToDirectory($zipPath, $tmp)

# si le zip contient un unique dossier racine, on descend dedans (meme logique que l'updater)
$entries = Get-ChildItem $tmp
$payload = $tmp
if ($entries.Count -eq 1 -and $entries[0].PSIsContainer) { $payload = $entries[0].FullName }
if (-not (Test-Path (Join-Path $payload "Echo.exe"))) { throw "Echo.exe absent de l'archive : publication incomplete ?" }

New-Item -ItemType Directory -Force $TargetDir | Out-Null
robocopy $payload $TargetDir /E /R:5 /W:1 /NFL /NDL /NJH /NJS /NP | Out-Null
if ($LASTEXITCODE -ge 8) { throw "Copie vers $TargetDir en echec (robocopy $LASTEXITCODE)." }
$global:LASTEXITCODE = 0
Remove-Item $tmp -Recurse -Force
Write-Host "Fichiers installes dans $TargetDir"

if ($DevTest) { Write-Host "(mode test : ni reglages, ni raccourci, ni lancement)"; exit 0 }

# -- regler l'URL de mise a jour sur la release GitHub -------------------------------
$setDir = Join-Path $env:APPDATA "Echo"
New-Item -ItemType Directory -Force $setDir | Out-Null
$setPath = Join-Path $setDir "settings.json"
$utf8 = New-Object System.Text.UTF8Encoding($false)
# Les mises a jour viennent de la derniere release GitHub (publique, sans compte) : le poste
# n'a plus besoin de garder le dossier partage synchronise apres l'installation.
$urlJson = '"https://github.com/romain-bernardeau/echo-recorder/releases/latest/download/version.json"'
if (-not (Test-Path $setPath)) {
    [System.IO.File]::WriteAllText($setPath, "{`n  ""update_url"": $urlJson`n}`n", $utf8)
} else {
    # On ne touche QUE la cle update_url, dans le texte, sans reserialiser le fichier.
    # Avant : ConvertFrom-Json puis ConvertTo-Json reecrivait tout le fichier, et si la lecture
    # echouait (fichier non conforme pour PowerShell 5.1), le fichier etait remplace par un
    # objet contenant SEULEMENT update_url : toute la configuration etait perdue.
    # Lecture en UTF-8 EXPLICITE : Get-Content sans -Encoding lit en ANSI sous PowerShell 5.1
    # (mojibake des accents, constate le 2026-09-18 sur un modele de compte rendu).
    $raw = [System.IO.File]::ReadAllText($setPath)
    $parsed = $true
    try { $null = $raw | ConvertFrom-Json } catch { $parsed = $false }
    $keyRx = New-Object System.Text.RegularExpressions.Regex('"update_url"\s*:\s*(null|"(?:[^"\\]|\\.)*")')
    if ($keyRx.IsMatch($raw)) {
        $new = $keyRx.Replace($raw, { param($m) '"update_url": ' + $urlJson }, 1)
    } elseif ($raw -match '^\s*\{\s*\}\s*$') {
        $new = "{`n  ""update_url"": $urlJson`n}`n"
    } elseif ($raw -match '^\s*\{') {
        $rx = New-Object System.Text.RegularExpressions.Regex('^\s*\{')
        $new = $rx.Replace($raw, { param($m) $m.Value + "`n  ""update_url"": $urlJson," }, 1)
    } else {
        $new = $null
    }
    if ($null -eq $new) {
        Write-Host "settings.json illisible : laisse intact. Reglez l'URL de mise a jour dans Echo (Parametres) : $vjPath" -ForegroundColor Yellow
    } else {
        if (-not $parsed) {
            # Fichier que PowerShell ne sait pas relire : copie de securite avant la retouche.
            $bak = $setPath + ".bak-" + (Get-Date -Format "yyyyMMdd-HHmmss")
            Copy-Item $setPath $bak -Force
            Write-Host "settings.json non standard : copie de securite $bak" -ForegroundColor Yellow
        }
        [System.IO.File]::WriteAllText($setPath, $new, $utf8)
    }
}
Write-Host "Mises a jour automatiques : $vjPath"

# -- raccourci menu Demarrer + lancement ----------------------------------------------
$lnk = Join-Path $env:APPDATA "Microsoft\Windows\Start Menu\Programs\Echo.lnk"
$shell = New-Object -ComObject WScript.Shell
$sc = $shell.CreateShortcut($lnk)
$sc.TargetPath = Join-Path $TargetDir "Echo.exe"
$sc.WorkingDirectory = $TargetDir
$sc.IconLocation = (Join-Path $TargetDir "Echo.exe") + ",0"
$sc.Description = "Echo, enregistreur de reunions"
$sc.Save()
Write-Host "Raccourci cree dans le menu Demarrer"

Start-Process (Join-Path $TargetDir "Echo.exe")
Write-Host ""
Write-Host "Echo est installe et demarre." -ForegroundColor Green
Write-Host "Derniere etape : collez votre cle API Mistral dans Parametres -> Integrations"
Write-Host "(guide pas-a-pas dans l'application : Parametres -> Aide, onglet 'Cle API Mistral')."
