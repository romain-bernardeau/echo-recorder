# get-ffmpeg.ps1 : (re)telecharge le ffmpeg audio (LGPL) que publish.ps1 met dans le zip et l'installeur.
#
# Ce ffmpeg est compile par la GitHub Action "ffmpeg-audio" (tools/ffmpeg-audio/build.sh) : audio
# seulement, sans composant GPL ni nonfree. La release ffmpeg-audio-<version> contient le binaire,
# les textes de licence et les sources exactes utilisees.
#
# Cache HORS du depot : %LOCALAPPDATA%\Echo-build\ffmpeg-audio\ (ffmpeg.exe + licences + BUILD-INFO.txt).
# publish.ps1 le detecte automatiquement. A ne relancer que pour changer de version de ffmpeg
# (mettre alors a jour $Version et $Sha256 ci-dessous).

$ErrorActionPreference = "Stop"
$Version = "9.0.2"
$Sha256 = "a66c2a13024df780482f9d70fa6618a62cce2da9c06b0c3e7a190f3efbfccf8d"
$Repo = "romainio/echo-recorder"

$cache = Join-Path $env:LOCALAPPDATA "Echo-build\ffmpeg-audio"
New-Item -ItemType Directory -Force $cache | Out-Null
$zip = Join-Path $cache "_ffmpeg-audio.zip"
$url = "https://github.com/$Repo/releases/download/ffmpeg-audio-$Version/ffmpeg-audio-$Version-win64.zip"

Write-Host "Telechargement $url"
$ProgressPreference = 'SilentlyContinue'
Invoke-WebRequest -Uri $url -OutFile $zip -UseBasicParsing
$actual = (Get-FileHash $zip -Algorithm SHA256).Hash.ToLowerInvariant()
if ($actual -ne $Sha256) { Remove-Item $zip -Force; throw "sha256 inattendu ($actual) : archive refusee." }

Get-ChildItem $cache -File | Where-Object { $_.Name -ne "_ffmpeg-audio.zip" } | Remove-Item -Force
Add-Type -AssemblyName System.IO.Compression.FileSystem
[System.IO.Compression.ZipFile]::ExtractToDirectory($zip, $cache)
Remove-Item $zip -Force

$exe = Join-Path $cache "ffmpeg.exe"
$enc = & $exe -hide_banner -encoders 2>$null | Select-String "libmp3lame"
if (-not $enc) { throw "Le ffmpeg telecharge n'a pas libmp3lame : build inattendu." }
Write-Host ("OK : ffmpeg {0} audio LGPL, {1:N1} Mo -> {2}" -f $Version, ((Get-Item $exe).Length / 1MB), $cache)
