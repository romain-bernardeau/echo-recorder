@echo off
title Echo (dev)
rem --- Lanceur de developpement : la build Release locale, a cote d'un Echo installe ---
rem Pour un test d'ENREGISTREMENT reel, quitte d'abord l'Echo installe (icone barre des taches > Quitter).
set "ECHO_ALLOW_MULTI=1"
set "ECHO_WEBVIEW_DIR=%LOCALAPPDATA%\Echo\webview-dotnet"
start "" "%~dp0src\Echo.App\bin\Release\net8.0-windows\Echo.exe"
