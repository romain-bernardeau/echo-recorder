# get-ffmpeg.ps1 : (re)telecharge le ffmpeg audio (LGPL) que publish.ps1 met dans le zip et l'installeur.
#
# Ce ffmpeg est compile par la GitHub Action "ffmpeg-audio" (tools/ffmpeg-audio/build.sh) : audio
# seulement, sans composant GPL ni nonfree. Chaque release d'Echo joint le binaire et les sources
# exactes utilisees (ffmpeg, LAME, build.sh), ce que publish.ps1 fait a partir de ce cache.
#
# Cache HORS du depot :
#   %LOCALAPPDATA%\Echo-build\ffmpeg-audio\       ffmpeg.exe + licences + BUILD-INFO.txt (bundle dans bin\)
#   %LOCALAPPDATA%\Echo-build\ffmpeg-audio-dist\  zip + sources + SHA256SUMS.txt (joints a la release)
#
# Usage :
#   .\tools\get-ffmpeg.ps1                 # depuis la derniere release d'Echo
#   .\tools\get-ffmpeg.ps1 -From <dossier> # depuis l'artefact "ffmpeg-audio" de l'Action (nouvelle version
#                                          # de ffmpeg : mettre alors a jour $Version et $Sha256 ci-dessous)

param([string]$From = "")

$ErrorActionPreference = "Stop"
$Version = "9.0.2"
# empreintes epinglees : rien n'est accepte s'il ne correspond pas exactement
$Sha256 = [ordered]@{
    "ffmpeg-audio-$Version-win64.zip" = "a66c2a13024df780482f9d70fa6618a62cce2da9c06b0c3e7a190f3efbfccf8d"
    "ffmpeg-$Version.tar.xz"          = "8c3850283eb25fa026482078a04051e0be17347b09ef81a0849bec15a96e002e"
    "lame-3.100.tar.gz"               = "ddfe36cab873794038ae2c1210557ad34857a4b6bdc515785d1da9e175b1da1e"
    "build.sh"                        = "71a8cf9514f6a8982b567966a3ffe74b8c9b4b09136043bedc470c471149b24c"
}
$Repo = "romainio/echo-recorder"

$root = Join-Path $env:LOCALAPPDATA "Echo-build"
$cache = Join-Path $root "ffmpeg-audio"
$dist = Join-Path $root "ffmpeg-audio-dist"
New-Item -ItemType Directory -Force $cache, $dist | Out-Null
$ProgressPreference = 'SilentlyContinue'

foreach ($name in $Sha256.Keys) {
    $dest = Join-Path $dist $name
    if ($From) {
        $src = Get-ChildItem $From -Recurse -File -Filter $name | Select-Object -First 1
        if (-not $src) { throw "$name absent de $From" }
        Copy-Item $src.FullName $dest -Force
    } else {
        $url = "https://github.com/$Repo/releases/latest/download/$name"
        Write-Host "Telechargement $url"
        Invoke-WebRequest -Uri $url -OutFile $dest -UseBasicParsing
    }
    $actual = (Get-FileHash $dest -Algorithm SHA256).Hash.ToLowerInvariant()
    if ($actual -ne $Sha256[$name]) { Remove-Item $dest -Force; throw "sha256 inattendu pour $name ($actual) : fichier refuse." }
}
$Sha256.GetEnumerator() | ForEach-Object { "$($_.Value)  $($_.Key)" } |
    Set-Content (Join-Path $dist "SHA256SUMS.txt") -Encoding ascii

Get-ChildItem $cache -File | Remove-Item -Force
Add-Type -AssemblyName System.IO.Compression.FileSystem
[System.IO.Compression.ZipFile]::ExtractToDirectory((Join-Path $dist "ffmpeg-audio-$Version-win64.zip"), $cache)

$exe = Join-Path $cache "ffmpeg.exe"
$enc = & $exe -hide_banner -encoders 2>$null | Select-String "libmp3lame"
if (-not $enc) { throw "Le ffmpeg telecharge n'a pas libmp3lame : build inattendu." }
Write-Host ("OK : ffmpeg {0} audio LGPL, {1:N1} Mo -> {2} (sources : {3})" -f $Version, ((Get-Item $exe).Length / 1MB), $cache, $dist)
