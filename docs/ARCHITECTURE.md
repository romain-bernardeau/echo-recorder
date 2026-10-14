# Echo, architecture (.NET 8 / C#)

Echo is a Windows meeting recorder that needs no bot in the meeting: it captures the PC's
audio (microphone + system output), reads speaker names from the Teams window through UI
Automation, transcribes with a cloud speech model and writes the minutes with an LLM.

## Stack

| Concern | Choice | Why |
|---|---|---|
| Runtime | .NET 8, `net8.0-windows`, self-contained publish | No admin rights, one folder per user |
| Shell | WPF + tray icon | Light, native, GPU-composited |
| Views | WebView2 hosting `ui/index.html` and `ui/widget.html` | One HTML/CSS design; the main view is disposed when hidden to keep RAM low |
| Audio | NAudio (WASAPI capture + endpoint loopback) + hand-written process-loopback interop | Two tracks, `mic.wav` and `system.wav`, on a shared clock |
| Teams | FlaUI (UIA3) on MTA worker threads | Meeting detection, active speaker, roster, subject |
| AI | Raw REST (`HttpClient` + `System.Text.Json`) | Mistral (Voxtral + chat) and Cloudflare Workers AI (Whisper + chat) |
| Secrets | DPAPI (`ProtectedData`, CurrentUser) | Per-user encrypted API keys |
| Audio tools | `ffmpeg.exe` next to the app (`bin/`): audio-only LGPL build, `tools/ffmpeg-audio` | Decoding imports, resampling, silence trimming, MP3 archive and upload |

## Projects

```
Echo.sln
├─ src/Echo.Core/   models, service interfaces, settings / secrets / paths, retention, recovery
├─ src/Echo.Audio/  IAudioCaptureService: WASAPI mic + loopback, gap filling, health
├─ src/Echo.Teams/  ITeamsMonitor (UIA) + Teams transcript import (.docx / .vtt)
├─ src/Echo.Ai/     transcription, diarisation merge, echo dedup, LLM postprocess, minutes, summary
├─ src/Echo.App/    WPF host: tray, windows, JS bridge, session state machine, updater
├─ tests/Echo.Tests/ xUnit tests (all HTTP stubbed, settings in a temp profile)
└─ ui/              HTML / CSS / JS of the main window and the recording widget
```

`Echo.Core` is the keystone: every module depends on it, modules never reference each other,
and `Echo.App` composes them.

## Pipeline

1. **Capture**: `mic.wav` + `system.wav` with JSON sidecars (start time, frames). Loopback gaps
   are filled live so both tracks stay on the wall clock. Teams speaker changes go to
   `speakers.jsonl`.
2. **Transcribe**: each track is resampled to 16 kHz mono, silences trimmed, encoded to MP3 and
   sent to the provider (long tracks are split on a quiet point). Timestamps are mapped back to
   the session clock.
3. **Merge**: segments are attributed to Teams names (system track) or to the user (mic track);
   echo of the speakers picked up by the mic is removed; an optional LLM pass corrects the text.
4. **Minutes and summary**: one LLM call, or map-reduce when the transcript exceeds the
   model's context window. Output: `.eml` (ready to send), `.docx`, `.md`, plus the short summary.

**Meeting folder** (`Echo.Core.Storage.SessionLayout`, the only code that knows it): at the root
`<Title> - Compte rendu.*` (EN `Minutes`), `<Title> - Transcript.txt` (corrected), `<Title> - Résumé.md`
(EN `Summary`), `<Title> - Audio.mp3`, `notes.md`, `Captures\`; everything else (json sidecars, status,
tracks, intermediates, raw transcript, logs) in the hidden `.echo\`. Reads fall back on the legacy
root; `SessionLayoutMigration` tidies older folders at startup (moves only, never overwrites).

## Distribution and updates

`publish.ps1` runs the tests, publishes self-contained, bundles `ffmpeg.exe` and writes a zip
plus `version.json` (version, relative zip URL, mandatory sha256), then builds `Echo-Setup.exe`
with Inno Setup (`deploy/Echo.iss`: per-user, no admin, no uninstaller of its own since Echo
registers itself in Installed apps at startup). All three go to the GitHub release. Installed copies check
`update_url` at startup, download and verify the zip, then a detached script swaps the files
and keeps a `.previous` copy for rollback.

## Build

```powershell
dotnet build Echo.sln -c Release
dotnet test tests/Echo.Tests/Echo.Tests.csproj
```

## Known fragile points

- Teams UI Automation selectors change with Teams releases: they live in
  `teams_selectors.json` (see `src/Echo.Teams/teams_selectors.example.json`).
- Voxtral `context_bias` must be repeated form fields matching `^[^,\s]+$` (else HTTP 422).
- A free Mistral key may answer 429 with a 0 requests/minute allowance on some models: that
  means "not in your plan", and Echo falls back to a free model.
