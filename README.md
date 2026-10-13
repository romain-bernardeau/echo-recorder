<p align="center"><b>English</b> · <a href="README.fr.md">Français</a> · <a href="README.es.md">Español</a></p>

# Echo

Echo records your Teams meetings (or in-person meetings) on Windows, transcribes who said what
and drafts minutes ready to send. No bot joins the meeting: Echo captures the PC's audio and reads
the speakers' names straight from the Teams window.

![Echo, home screen](assets/screenshots/home-en.png)

## What Echo does

- **Recording** of two tracks (your microphone and the other participants), started by hand, with
  a keyboard shortcut or when a Teams meeting is detected. A small widget stays on screen during
  the meeting: timestamped notes, screenshots, markers.
- **Transcription** with Mistral (Voxtral) or Cloudflare Workers AI (Whisper), with speakers named
  from Teams.
- **Minutes** (.eml ready to open in Outlook, .docx, .md) and a short summary, with custom minutes
  templates and a domain vocabulary. Long meetings are summarised in parts automatically.
- **Import** of an audio file or of a Teams transcript (.docx / .vtt).
- **Search** across all meetings, missions, storage management, automatic updates.

![Echo, generated minutes](assets/screenshots/minutes-en.png)

![Echo, recording widget](assets/screenshots/widget-en.png)

## Privacy

Recordings stay on your PC. Only the audio to transcribe and the text to summarise are sent to the
AI provider you choose, with **your own API key**, stored encrypted (Windows DPAPI). Let the
participants know the meeting is being recorded.

## Installation

Downloadable releases with an installer are being set up in [Releases](../../releases). Echo
installs in your user profile (`%LOCALAPPDATA%\Programs\Echo`), without admin rights, and updates
itself. On first launch, paste your API key in Settings > Providers: models tagged **Free** work
with a free key.

Requirements: Windows 10 or 11 with WebView2, Microsoft Teams (new version). The interface is
available in French and English.

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

Copyright (C) 2026 Echo contributors.

Echo is free software under the GNU GPL v3 or later (SPDX: `GPL-3.0-or-later`), see `LICENSE`.
Third-party components: `THIRD-PARTY-NOTICES.md`.
