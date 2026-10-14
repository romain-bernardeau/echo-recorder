# uninstall.ps1 : desinstalle Echo (installe par-utilisateur, sans admin).
# Embarque a cote de Echo.exe ; appele par Parametres Windows -> Applications -> Echo ->
# Desinstaller (ou manuellement). Ne touche JAMAIS vos enregistrements ni vos reglages.

$ErrorActionPreference = "SilentlyContinue"
$installDir = $PSScriptRoot

# 0. garde-fou : ce dossier doit etre une installation d'Echo (le dossier entier est supprime)
if (-not (Test-Path (Join-Path $installDir "Echo.exe")) -or -not (Test-Path (Join-Path $installDir "Echo.dll"))) {
    Write-Host "Ce dossier n'est pas une installation d'Echo : rien n'est supprime."
    exit 1
}

# 1. Echo ne doit pas tourner : jamais de fermeture forcee (une reunion peut etre en cours d'enregistrement)
$running = Get-Process -Name "Echo" | Where-Object { $_.Path -and $_.Path.StartsWith($installDir, [StringComparison]::OrdinalIgnoreCase) }
if ($running) {
    (New-Object -ComObject WScript.Shell).Popup("Quittez Echo (icone pres de l'horloge, puis Quitter), puis relancez la desinstallation.", 0, "Echo", 48) | Out-Null
    exit 2
}

# 2. raccourci menu Demarrer et lancement au demarrage de Windows
Remove-Item (Join-Path $env:APPDATA "Microsoft\Windows\Start Menu\Programs\Echo.lnk") -Force
Remove-ItemProperty "HKCU:\Software\Microsoft\Windows\CurrentVersion\Run" -Name "Echo" -Force

# 3. entree "Applications installees"
Remove-Item "HKCU:\Software\Microsoft\Windows\CurrentVersion\Uninstall\Echo" -Recurse -Force

# 4. dossier programme : supprime par un process detache (ce script vit dedans)
$cmd = Join-Path $env:TEMP ("echo_uninstall_" + [Guid]::NewGuid().ToString("N") + ".cmd")
@"
@echo off
ping 127.0.0.1 -n 3 >nul
rmdir /s /q "$installDir" 2>nul
del /q "%~f0"
"@ | Set-Content -Path $cmd -Encoding OEM
Start-Process cmd.exe -ArgumentList "/c", "`"$cmd`"" -WindowStyle Hidden

Write-Host "Echo desinstalle. Vos enregistrements et reglages sont conserves."