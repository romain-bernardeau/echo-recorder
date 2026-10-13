# get-ffmpeg.ps1 — (re)telecharge le ffmpeg "essentials" que publish.ps1 bundle dans le zip.
#
# Le binaire est mis en cache HORS du depot (pour ne pas alourdir OneDrive) a :
#   %LOCALAPPDATA%\Echo-build\ffmpeg\ffmpeg.exe
# publish.ps1 le detecte automatiquement a cet emplacement.
#
# Source : gyan.dev (builds Windows officiels, references par ffmpeg.org). Le build
# "essentials" (~97 Mo) suffit largement a Echo : il embarque libmp3lame (encodage MP3),
# soxr (reechantillonnage), silencedetect et amix. A ne relancer que pour mettre a jour ffmpeg.

$ErrorActionPreference = "Stop"
$cache = Join-Path $env:LOCALAPPDATA "Echo-build\ffmpeg"
New-Item -ItemType Directory -Force $cache | Out-Null
$zip = Join-Path $cache "_essentials.zip"
$exe = Join-Path $cache "ffmpeg.exe"

Write-Host "Telechargement ffmpeg essentials -> $cache"
$ProgressPreference = 'SilentlyContinue'
Invoke-WebRequest -Uri "https://www.gyan.dev/ffmpeg/builds/ffmpeg-release-essentials.zip" -OutFile $zip -UseBasicParsing

Add-Type -AssemblyName System.IO.Compression.FileSystem
$arch = [System.IO.Compression.ZipFile]::OpenRead($zip)
try {
    $entry = $arch.Entries | Where-Object { $_.FullName -match 'bin/ffmpeg\.exe$' } | Select-Object -First 1
    if (-not $entry) { throw "ffmpeg.exe introuvable dans l'archive." }
    [System.IO.Compression.ZipFileExtensions]::ExtractToFile($entry, $exe, $true)
}
finally { $arch.Dispose() }
Remove-Item $zip -Force

$enc = & $exe -hide_banner -encoders 2>$null | Select-String "libmp3lame"
if (-not $enc) { throw "Le ffmpeg telecharge n'a pas libmp3lame — build inattendu." }
Write-Host ("OK : {0:N0} Mo, libmp3lame present -> {1}" -f ((Get-Item $exe).Length / 1MB), $exe)
