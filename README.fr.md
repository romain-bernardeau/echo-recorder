<p align="center"><img src="assets/echo-logo.png" width="96" alt="Logo Echo"></p>
<h1 align="center">Echo</h1>
<p align="center">Enregistrez vos réunions Teams, obtenez la transcription et un compte rendu prêt à envoyer. Sans bot dans la réunion.</p>
<p align="center">
  <img src="https://img.shields.io/badge/platform-Windows%2010%20%7C%2011-0D2F4B" alt="Plateforme : Windows 10 et 11">
  <img src="https://img.shields.io/badge/works%20with-Microsoft%20Teams-2f6db0" alt="Fonctionne avec Microsoft Teams">
  <img src="https://img.shields.io/badge/.NET-8-512BD4?logo=dotnet&logoColor=white" alt=".NET 8">
  <img src="https://img.shields.io/badge/license-MIT-2ea44f" alt="Licence : MIT">
</p>
<p align="center"><a href="README.md">English</a> · <b>Français</b> · <a href="README.es.md">Español</a></p>

> **Uniquement Windows et Microsoft Teams pour l'instant.** Les réunions en présentiel enregistrées
> sur un PC Windows fonctionnent aussi. macOS, Google Meet et Zoom sont prévus dans la
> [feuille de route](#feuille-de-route).

Echo enregistre vos réunions Teams (ou en présentiel) sur Windows, transcrit qui a dit quoi
et rédige un compte rendu prêt à envoyer. Aucun robot ne rejoint la réunion : Echo capte le son
du PC et lit les noms des intervenants directement dans la fenêtre Teams.

![Echo, écran d'accueil](assets/screenshots/home-fr.png)

## Ce que fait Echo

- **Enregistrement** de deux pistes (votre micro et le son des participants), lancé à la main,
  par raccourci clavier ou à la détection d'une réunion Teams. Un petit widget reste à l'écran
  pendant la réunion : notes horodatées, captures d'écran, marqueurs.
- **Transcription** avec Mistral (Voxtral) ou Cloudflare Workers AI (Whisper), locuteurs nommés
  grâce aux noms lus dans Teams.
- **Compte rendu** (.eml prêt à ouvrir dans Outlook, .docx, .md) et résumé court, avec des modèles
  de CR personnalisables et un vocabulaire métier. Les longues réunions sont résumées par parties
  automatiquement.
- **Import** d'un fichier audio ou d'une transcription Teams (.docx / .vtt).
- **Recherche** dans toutes les réunions, missions, gestion du stockage, mises à jour automatiques.

![Echo, compte rendu généré](assets/screenshots/minutes-fr.png)

![Echo, widget d'enregistrement](assets/screenshots/widget-fr.png)

## Confidentialité

Les enregistrements restent sur votre PC. Seul l'audio à transcrire et le texte à résumer sont
envoyés au fournisseur d'IA que vous choisissez, avec **votre propre clé API**, stockée chiffrée
(DPAPI Windows). Prévenez les participants que la réunion est enregistrée.

## Installation

La publication des versions dans [Releases](../../releases), avec un installeur téléchargeable,
est en cours de mise en place. Echo s'installe dans votre profil (`%LOCALAPPDATA%\Programs\Echo`),
sans droits administrateur, et se met à jour tout seul. Au premier lancement, collez votre clé API
dans Paramètres > Fournisseurs : les modèles marqués **Free** fonctionnent avec une clé gratuite.

Prérequis : Windows 10 ou 11 avec WebView2, Microsoft Teams (nouvelle version). L'interface est
disponible en français et en anglais.

## Feuille de route

- **Versions publiées sur GitHub**, avec un installeur téléchargeable et des mises à jour servies
  depuis GitHub.
- Version **macOS**.
- Prise en charge de **Google Meet** et **Zoom** (détection de la réunion et noms des intervenants).
- Aide en ligne sur GitHub, bouton « Signaler un problème » dans l'application.

## Compiler

```powershell
dotnet build Echo.sln -c Release
dotnet test tests/Echo.Tests/Echo.Tests.csproj
```

.NET 8 SDK, WPF + WebView2 (interface dans `ui/`), NAudio (capture WASAPI), FlaUI (lecture de
Teams). Voir `docs/ARCHITECTURE.md`. `ffmpeg.exe` est attendu dans `bin/` à côté de l'exe
(`tools/get-ffmpeg.ps1` le télécharge).

## Retours

Bugs et idées sont les bienvenus dans [Issues](../../issues).

## Licence

[MIT](LICENSE), Copyright (c) 2026 Echo contributors. Composants tiers (dont FFmpeg, livré comme
programme séparé sous GPL) : `THIRD-PARTY-NOTICES.md`.

Microsoft Teams, Windows, Google Meet, Zoom et macOS sont des marques de leurs propriétaires
respectifs. Echo est un projet indépendant, ni affilié à ces sociétés ni approuvé par elles.
