<p align="center"><img src="assets/echo-logo.png" width="96" alt="Echo logo"></p>
<h1 align="center">Echo</h1>
<p align="center">Record your Teams meetings, get the transcript and minutes ready to send. No bot in the meeting.</p>
<p align="center">
  <img src="https://img.shields.io/badge/platform-Windows%2010%20%7C%2011-0D2F4B" alt="Platform: Windows 10 and 11">
  <img src="https://img.shields.io/badge/works%20with-Microsoft%20Teams-2f6db0" alt="Works with Microsoft Teams">
  <img src="https://img.shields.io/badge/.NET-8-512BD4?logo=dotnet&logoColor=white" alt=".NET 8">
  <img src="https://img.shields.io/badge/license-MIT-2ea44f" alt="License: MIT">
</p>
<p align="center"><b>English</b> · <a href="README.fr.md">Français</a> · <a href="README.es.md">Español</a></p>

> **Windows and Microsoft Teams only for now.** In-person meetings recorded on a Windows PC work
> too. macOS, Google Meet and Zoom are on the [roadmap](#roadmap).

Echo records your Teams meetings (or in-person meetings) on Windows, transcribes who said what
and drafts minutes ready to send. No bot joins the meeting: Echo captures the PC's audio and reads
the speakers' names straight from the Teams window.

**Help:** the [user guide](help/README.md) covers setup, recording, API keys and troubleshooting.

![Echo, home screen](assets/screenshots/home-en.png)

## What Echo does

- **Recording** of two tracks (your microphone and the other participants), started by hand, with
  a keyboard shortcut or when a Teams meeting is detected. A small widget stays on screen during
  the meeting: timestamped notes, screenshots, markers.
- **Transcription** with Mistral (Voxtral), Google Gemini or Cloudflare Workers AI (Whisper), with
  speakers named from Teams.
- **Minutes** (.eml ready to open in Outlook, .docx, .md) and a short summary, with custom minutes
  templates and a domain vocabulary, written by Mistral, Gemini, Cloudflare or any model on OpenRouter. Long
  meetings are summarised in parts automatically.
- **Import** of an audio file or of a Teams transcript (.docx / .vtt).
- **Search** across all meetings, missions, storage management, automatic updates.

![Echo, generated minutes](assets/screenshots/minutes-en.png)

![Echo, recording widget](assets/screenshots/widget-en.png)

## Privacy

Recordings stay on your PC. Only the audio to transcribe and the text to summarise are sent to the
AI provider you choose, with **your own API key**, stored encrypted (Windows DPAPI). Let the
participants know the meeting is being recorded.

## Installation

**[Download Echo-Setup.exe](https://github.com/romainio/echo-recorder/releases/latest/download/Echo-Setup.exe)**
and double-click it. Echo installs in your user profile (`%LOCALAPPDATA%\Programs\Echo`), without
admin rights, and updates itself. On first launch, paste your API key in Settings > Providers:
models tagged **Free** work with a free key.

The installer is not signed yet: if Windows shows "Windows protected your PC", click "More info",
then "Run anyway". All versions are in [Releases](../../releases).

Requirements: Windows 10 or 11 with WebView2, Microsoft Teams (new version). The interface is
available in French and English.

## Roadmap

- **macOS** version.
- **Google Meet** and **Zoom** support (meeting detection and speaker names).
- "Report a problem" button in the app.
- Signed installer (no Windows warning on first install).

## Building

```powershell
dotnet build Echo.sln -c Release
dotnet test tests/Echo.Tests/Echo.Tests.csproj
```

.NET 8 SDK, WPF + WebView2 (interface in `ui/`), NAudio (WASAPI capture), FlaUI (reading Teams).
See `docs/ARCHITECTURE.md`. `ffmpeg.exe` is expected in `bin/` next to the exe
(`tools/get-ffmpeg.ps1` downloads it).

## Feedback

Bugs and ideas are welcome in [Issues](../../issues).

## License

[MIT](LICENSE), Copyright (c) 2026 Echo contributors. Third-party components (including FFmpeg,
shipped as a separate audio-only program under LGPL, source attached to every release): `THIRD-PARTY-NOTICES.md`.

Microsoft Teams, Windows, Google Meet, Zoom and macOS are trademarks of their respective owners.
Echo is an independent project, not affiliated with or endorsed by them.
