# uninstall.ps1 — desinstalle Echo (installe par-utilisateur, sans admin).
# Embarque a cote de Echo.exe ; appele par Parametres Windows -> Applications -> Echo ->
# Desinstaller (ou manuellement). Ne touche JAMAIS vos enregistrements ni vos reglages.

$ErrorActionPreference = "SilentlyContinue"
$installDir = $PSScriptRoot

# 1. fermer Echo s'il tourne
Get-Process -Name "Echo" -ErrorAction SilentlyContinue | Stop-Process -Force -ErrorAction SilentlyContinue
Start-Sleep -Milliseconds 800

# 2. raccourci menu Demarrer
Remove-Item (Join-Path $env:APPDATA "Microsoft\Windows\Start Menu\Programs\Echo.lnk") -Force -ErrorAction SilentlyContinue

# 3. entree "Applications installees"
Remove-Item "HKCU:\Software\Microsoft\Windows\CurrentVersion\Uninstall\Echo" -Recurse -Force -ErrorAction SilentlyContinue

# 4. dossier programme — supprime par un process detache (ce script vit dedans)
$cmd = Join-Path $env:TEMP "echo_uninstall.cmd"
@"
@echo off
ping 127.0.0.1 -n 3 >nul
rmdir /s /q "$installDir" 2>nul
del /q "%~f0"
"@ | Set-Content -Path $cmd -Encoding OEM
Start-Process cmd.exe -ArgumentList "/c", $cmd -WindowStyle Hidden

Write-Host "Echo desinstalle. Vos enregistrements et reglages sont conserves."
