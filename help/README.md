<p align="center"><b>English</b> · <a href="README.fr.md">Français</a> · <a href="README.es.md">Español</a></p>

# Echo help

Echo records your Teams meetings (or in-person meetings) on Windows, transcribes who said what and
writes minutes ready to send. No bot joins the meeting: everything happens on your PC.

> **Windows and Microsoft Teams only for now.** The interface is available in English and French.

**Contents**

- [Getting started](#getting-started)
- [Recording and the widget](#recording)
- [After the meeting](#after-the-meeting)
- [Get an API key](#api-keys): [Mistral](#mistral) · [Cloudflare](#cloudflare) · [Google Gemini](#gemini) · [OpenRouter](#openrouter)
- [Troubleshooting](#troubleshooting)
- [Every setting at a glance](#settings)

<a id="getting-started"></a>

## Getting started

### Three steps

1. **Add an API key.** You do this once. Echo needs a key from an AI provider to transcribe and to
   write the minutes. Pick one in [Get an API key](#api-keys), then paste it in
   **Settings > Providers** and click **"Test"**.
2. **Start a recording.** Click **"New recording"** at the top of the sidebar. Echo captures the
   Teams meeting sound on its own, or your microphone for an in-person meeting.
3. **Stop and get the minutes.** At the end, click **"Stop & generate minutes"**. Echo transcribes,
   then writes the minutes. You find them on the meeting page.

### The main window

**Title bar**

- **Search** (`Ctrl K`): type a name and the meeting list filters as you type.
- **Sun / moon**: switches between light and dark theme. The full setting, with "System", is in
  Settings > General.
- **The close button** does not quit Echo. The app keeps running in the **notification area**, near
  the clock, and keeps watching your meetings. Click its icon to reopen the window.

**Sidebar**

- **New recording** starts a meeting. While recording, the same button stops it.
- **Import** reuses a Teams transcript (.docx or .vtt) **or an audio file** (.mp3, .m4a, .wav,
  .mp4 and more). Echo creates the meeting page, then the minutes and a summary.
- **Folder** creates a mission folder (a name, a colour) to sort your meetings.
- **Home** and **All meetings** open the overview and the full list. The counter shows the total.
- **Folders** lists your missions. A click filters the list on that folder, "+" creates one.
- **Open local folder** opens the Windows folder that holds every recording.
- **Your name**, at the very bottom, opens **Settings**.

**Home**

- **Three counters**: meetings, minutes produced, total recorded time.
- **The recording banner** shows during a meeting: timer, meeting name, a **Widget** button (brings
  the floating widget back if hidden) and **"Stop & generate minutes"**. Warnings (microphone,
  sound) show on the right.
- **Recent meetings** and **Folders** give direct access to meetings and missions.

**All meetings**

- **Filters** by name, mission folder, status and period (7, 30 or 90 days).
- **Select** turns on multi-selection: tick meetings, then **Generate transcript** (batch re-run) or
  **Delete**. "Select all" acts on the filtered list.
- **The trash icon** on a card deletes that meeting and its whole folder.

| Status | What it means |
|---|---|
| **Minutes** | Transcript and minutes generated. The page is complete. |
| **Recorded** | Audio captured, but no transcript or no minutes yet. |
| **Processing** | Transcription or writing in progress. |
| **Waiting for API** | The AI provider was saturated (429) or down. Echo retries by itself after 10 min, then 30, then 60. Nothing to do, the audio is kept. |
| **Error** | A step failed for a reason that waiting will not fix (key refused, model not in your plan). The notification gives the cause. Re-run from the page with "Generate minutes" or "Re-run transcription". |

<a id="recording"></a>

## Recording and the widget

While recording, a banner shows on the home screen and a small **floating widget** stays on top of
your windows. You keep everything at hand without leaving the meeting.

### The floating widget

A small card with the timer, notes, key moments, screenshots and a stop button. Its header shows
the **meeting name** and its state ("Recording in progress", or a warning if something is wrong).
Drag the header to **move** it, and the bottom-right corner to **change its width**. The height
follows the content.

**Header buttons**

| Button | What it does |
|---|---|
| **Pin** | Keeps the widget on top (the default). Click again to let it go behind other windows. |
| **Fold into rail** | Folds the widget into a thin vertical bar. Handy when you share your screen. |
| **Hide** | Hides the widget. Recording goes on. The "Widget" button of the home banner brings it back. |

**Notes and last capture**

- **Take a note**: click "Click to type a note" (or the note button at the bottom), type, then press
  `Enter`. Each note goes to `notes.md` and shows in the meeting's "Notes & key moments" tab.
- **Timestamp** (clock button): on by default, it stamps each note with the meeting time ("12:40").
  Click it to write a note without a timestamp.
- **Link to capture** (chain button): attaches the note to the last screenshot (a "[capture]"
  prefix). Useful to comment on a specific slide.
- **The "Last capture" thumbnail** appears after a screenshot, with the time and a counter. A click
  opens the image.

**Bottom buttons**

| Button | What it does |
|---|---|
| **Notes** | Opens or closes the note panel. |
| **Key moment ⚑** | Marks this instant with a time-stamped "Key moment" note, to find the passage later. |
| **Screenshot** | Captures the screen (the shared slides). The image goes to the meeting's **Screens** tab and shows in the widget. |
| **Stop** | Stops recording and starts processing (transcription, then minutes and summary if the automations are on). |

**Rail mode (folded widget)**

Folded, the widget is a vertical bar about 50 px wide: a pulsing red dot (recording is running), a
handle to move it, and four buttons: **Unfold** (back to the full card), **Screenshot**,
**Key moment ⚑** and **Stop**, the same as on the card.

### Detection and automations

Everything is in **Settings > Recording**.

- **Meeting detection**: Echo watches Teams and **offers** to record as soon as you join a call.
- **Automatic recording**: no question asked, every detected Teams meeting is recorded (this also
  turns detection on). When you leave the meeting, Echo stops by itself after a short delay, then
  runs transcription, minutes and summary according to your settings.
- **Automatic screenshots**: shared slides are captured regularly, on top of your own captures.
- **Global keyboard shortcut**: starts or stops recording from any app (`Ctrl+Alt+R` by default).

**In person**, when no Teams meeting is detected, Echo records the microphone only. Put the PC in the
middle of the table.

> The participants' sound must come out of **this PC** (speakers or headset). If it comes out
> somewhere else (another computer, a TV), Echo records the microphone only and warns you.

<a id="after-the-meeting"></a>

## After the meeting

Click a meeting to open its **page**. It has tabs: Summary, Transcript, Minutes, Screens, Attendees
and Notes.

### Page header

- **The pencil** next to the title renames the meeting (the page and the files).
- **The folder icon** opens the meeting's Windows folder.
- **The "No folder" menu** puts the meeting in a mission folder.
- **Generate minutes** opens the Minutes tab and writes the minutes with the chosen template.
- **Re-run transcription** redoes everything from the audio. Useful after a vocabulary change, or if
  a transcription failed. The audio is never re-recorded, only analysed again.
- **Share & export** opens the files menu (see below).

### Summary tab

The gist of the meeting in 5 to 10 short bullets, true to the transcript, nothing invented. It is
generated on demand ("Generate summary") or automatically. **"Regenerate"** redoes it, for example
after you named the speakers.

### Transcript tab

The verbatim is split by speaking turn. Each turn has a time and a speaker, read from the Teams
window (again, no bot in the meeting).

- **Search** filters the turns as you type, speaker names included.
- **Copy** copies the whole time-stamped verbatim.
- **Name a speaker**: unknown voices show as "Speaker N" with a pencil. Give the real name once and
  it applies to **every** turn of that voice. Then regenerate the minutes with the right names.
- **▶ Listen to the passage**: hover a turn and a play button plays that excerpt, with a few seconds
  before and after.
- **Full-track player** at the top of the tab. Click a turn's time to jump there.

### Business vocabulary

In **Settings > Vocabulary**, list the words the transcription gets wrong: client names, projects,
acronyms, tools. They guide speech recognition and are fixed in both the transcript and the minutes.
One entry per line: a **term** alone, or **variant1|variant2=>Term** to replace known mistakes with
the right spelling.

The **"Copy-paste prompt"** button copies a ready-made prompt. Paste it in your AI assistant with
your mission documents (notes, e-mails, glossary): it gives you the list in the right format. A
vocabulary can be **general** or tied to a **mission folder** ("Applies to").

### Minutes tab and templates

Pick a **template** and the **output language** (the meeting's language, or French, English,
Spanish, German), then click **Generate**. The minutes show in the tab and the files listed under
"Generated files" are ready to send. Generating again replaces the previous minutes.

- **Two built-in templates**: **Detailed** (structured, with decisions, actions and points per topic)
  and **Summary** (just the essentials).
- **Your own templates**: in **Settings > Minutes**, start from a base, add **guidelines** ("put the
  actions in a table", "start with the risks") and an **example to imitate** (paste minutes whose
  style you like). The default template is the one used by the automations.
- **Formats**: .eml (an Outlook draft ready to send), .md and .docx, as you choose in Settings.

### Screens tab

Every screenshot of the meeting, manual or automatic, in a grid. A click opens the image. The pencil
adds a note linked to that capture.

### Attendees tab and e-mails

Teams only gives **names**. Enter a person's e-mail **once**: Echo remembers it for every meeting
where they appear. These e-mails fill in the .eml draft of the minutes.

### Notes & key moments tab

Everything typed during the meeting: time-stamped notes, **key moments ⚑** and notes linked to
captures. **Edit** opens free Markdown editing. Fix, add, then **Save**: the meeting's `notes.md` is
updated.

### Share & export

- **Meeting folder**: all the files, in Explorer.
- **Minutes**: each generated file (.eml, .md, .docx) opens directly. The .eml opens in Outlook,
  ready to send.
- **Audio**: the meeting's listening MP3.
- **Claude folder (.md)**: a folder ready for analysis in an AI assistant, with the meeting context,
  the corrected transcript and the notes in Markdown.

### Mission folders

Create a folder ("Folder" button or "+" in the sidebar), then assign each meeting from its page with
the "No folder" menu. The folder becomes a **filter** everywhere and can have its **own vocabulary**
(Settings > Vocabulary > "Applies to"), added on top of the general one.

### Import a Teams transcript or an audio file

The **Import** button (sidebar, or Settings > Providers > Microsoft Teams) accepts:

- a transcript exported from Teams (.docx or .vtt): Echo creates the page with attendees and
  verbatim, then can write minutes and a summary;
- an **audio or video file**: phone or recorder file, voice note, Teams recording (.mp4).
  Formats: .mp3, .m4a, .wav, .aac, .wma, .ogg, .opus, .flac, .amr, .3gp, .mp4, .mov, .mkv, .webm,
  .avi. Echo creates the meeting (name = file name, time = estimated start), transcribes it with
  your provider, then writes the minutes and the summary. The original file is never changed. With
  a single track and no Teams attendees, voices are numbered ("Participant 0", "Participant 1"...)
  when the engine can tell them apart (Voxtral, Gemini). Rename them from the page.

<a id="api-keys"></a>

## Get an API key

Echo sends the audio to transcribe and the text to summarise to the AI provider you choose, with
**your own key**. The key is stored encrypted on your PC (Windows DPAPI).

| Provider | Transcription | Minutes, summary, correction | Free offer |
|---|---|---|---|
| [Mistral](#mistral) | Yes (Voxtral) | Yes | Yes, with limits |
| [Cloudflare Workers AI](#cloudflare) | Yes (Whisper) | Yes | Yes, a daily quota |
| [Google Gemini](#gemini) | Yes | Yes | Yes, with limits |
| [OpenRouter](#openrouter) | No | Yes | Yes, models marked `:free` |

**How it works in Echo**

- Everything is in **Settings > Providers**. Choose the **transcription engine** and the
  **minutes & summary engine**. They can be different providers.
- Paste each key, click **"Save"**, then **"Test"**. The line under the button tells you if the key
  works.
- Models with the **Free** tag run on the provider's free offer. Turn on **"Show model details"** to
  see and change the model used for each task.
- **Automatic fallback** (on by default): if a provider is saturated or refuses the model, Echo
  switches to the other provider whose key is set.
- Tip: set up two providers (for example Mistral and Cloudflare). If one fails, the other takes over.

Prices, limits and screens of these services change often. The steps below were checked on
13 October 2026.

<a id="mistral"></a>

### Mistral

Transcription with Voxtral, plus minutes, summary and correction.

**Create the key**

1. Go to [console.mistral.ai](https://console.mistral.ai) and sign up or sign in.
2. If asked, choose a plan. The free plan is called **"Free mode"** (formerly "Experiment").
3. Open **"API Keys"** in the menu.
4. Click **"Create new key"**. Give it a name (for example `Echo`), keep the default workspace and
   expiration, then click **"Create new key"** again.
5. Copy the key right away. **It is shown only once.** If you lose it, create a new one.
6. In Echo: **Settings > Providers > Mistral**, paste the key in **"Mistral API key"**, click
   **"Save"**, then **"Test"**.

Use a key from the API console. A key made for other Mistral products (for example "Vibe" or
"Mistral Code") does not work here.

**Free offer and limits**

- Mistral's documentation is not consistent: one page says no credit card is needed, another says
  keys stay inactive until you add a payment method. Depending on your account, you may be asked for
  a card or a phone number.
- Your limits are in the console: **Admin Panel > API > Limits**.
- Voxtral in Free mode is not documented. In our test on 13 October 2026, `voxtral-mini` worked on a
  free key (60 requests per minute), as did `ministral-8b`, `ministral-3b` and `open-mistral-nemo`.
- Some models (for example `mistral-small` or `mistral-medium`) answered 429 with a limit of
  **0 requests per minute**: they are not in your plan. Echo then switches to a model with the
  **Free** tag.

**Privacy**

- On the free plan, Mistral may use your data to improve its models. You can opt out in
  **Admin > Privacy > "Anonymous improvement data"**.
- On a paid plan, your data is not used for training.

**Common errors**

| Error | Meaning | What to do |
|---|---|---|
| 401 | Key wrong, revoked or expired | Create a new key and paste it again. |
| 402 | Payment needed | Add a payment method in the console, or use another provider. |
| 429 | Too many requests, or quota reached | Wait. Echo retries, then falls back to the other provider. |
| 429 with "0 requests per minute" | Model not in your plan | Choose a model with the **Free** tag. Echo does it by itself. |

<a id="cloudflare"></a>

### Cloudflare Workers AI

Transcription with Whisper (`@cf/openai/whisper-large-v3-turbo`), plus minutes, summary and
correction. Cloudflare needs **two values**: an **API token** and your **Account ID**.

**Create the token**

1. Go to [dash.cloudflare.com](https://dash.cloudflare.com) and sign up or sign in.
2. Open **Workers AI** (under "AI" in the menu).
3. Click **"Use REST API"**.
4. Click **"Create a Workers AI API Token"**, check the permissions, then click
   **"Create API Token"**.
5. Click **"Copy API Token"**. **It is shown only once.** Tokens start with `cfut_`.
6. On the same page, copy your **Account ID** ("Get Account ID"). It is 32 characters long.
7. In Echo: **Settings > Providers > Cloudflare Workers AI**, paste the Account ID in
   **"Cloudflare account id"**, paste the token in **"Cloudflare API token"**, click **"Save"**,
   then **"Test"**.

Other way: **My Profile > API Tokens > Create Token**, then a custom token with the
**Workers AI: Read** and **Workers AI: Edit** permissions.

**Free offer and limits**

- The free plan gives **10,000 neurons per day**, reset at 00:00 UTC. There is no overage on the
  free plan: once the quota is used, requests are refused until the next day.
- Whisper turbo costs about 46.63 neurons per audio minute. By our estimate, that is about
  **214 minutes, so roughly 3 h 30 of audio per day**. This is an estimate: the minutes and summary
  use the same quota.
- Whisper does not separate voices. Echo names the turns from the track (you or the other
  participants) and the Teams names seen during the meeting.

**Privacy**

Cloudflare says it does not use your data to train models, on free and paid plans.

**Common errors**

| Code | Meaning | What to do |
|---|---|---|
| 3036 | Daily quota used up | Wait until 00:00 UTC, or use another provider. |
| 3040 | No capacity right now | Try again later. Echo retries by itself. |
| 5035 | Model reserved for paid plans | Choose a model with the **Free** tag. |
| 3006 | Request too large | Echo sends audio in 10-minute pieces. If it still happens, re-run the transcription. |
| 3042 | Invalid model | Check the model name, or choose one from the list. |
| "Failed to decode audio file" | The transcription model does not accept Echo's audio format | Use `@cf/openai/whisper-large-v3-turbo`, then "Re-run transcription". |

<a id="gemini"></a>

### Google Gemini

Transcription, minutes, summary and correction with a Google AI Studio key.

**Create the key**

1. Go to [aistudio.google.com/apikey](https://aistudio.google.com/apikey) and sign in with a Google
   account.
2. Click **"Get API key"** if needed, then **"Create API key"**.
3. New accounts get a project and a key created for them. Otherwise, pick a project, or bring one in
   with **"Import projects"**.
4. Copy the key. Good practice: restrict it to the **Gemini API**.
5. In Echo: **Settings > Providers > Google Gemini**, paste the key in **"Google API key (Gemini)"**,
   click **"Save"**, then **"Test"**.

With a work Google account (Google Workspace), the button can be greyed out if your administrator
did not give you the right permissions. Ask them, or use a personal account.

**Free offer and limits**

- Free without billing. The flash models carry the **Free** tag in Echo.
- Limits are set **per project**, not per key. See them at
  [aistudio.google.com/rate-limit](https://aistudio.google.com/rate-limit).
- Daily quotas reset at midnight, Pacific time.

**Privacy**

- Google says that in the EU and EEA, the paid-service privacy rules (no training on your data) also
  apply to the free tier. One clause of the terms ("only Paid Services for API clients in the EEA")
  is ambiguous. If in doubt, turn on billing or use another provider.
- Outside these regions, free-tier data may be used to improve Google's products.

**Common errors**

| Error | Meaning | What to do |
|---|---|---|
| 400 / 403 | Key invalid or not allowed for the Gemini API | Check the key restrictions, or create a new key. |
| 429 | Project quota reached | Wait for the reset, or switch the engine. Echo falls back by itself. |

<a id="openrouter"></a>

### OpenRouter

Minutes, summary and correction only. **OpenRouter does not transcribe**: pair it with Mistral,
Cloudflare or Gemini for transcription.

**Create the key**

1. Go to [openrouter.ai/keys](https://openrouter.ai/keys) and sign up or sign in.
2. Click the button that creates a key ("Create API Key" or "Create Key", depending on the page).
3. Give it a name and choose a credit limit (or no limit). A limit is a good idea.
4. Copy the key. **It is shown only once.**
5. In Echo: **Settings > Providers > OpenRouter**, paste the key in **"OpenRouter API key"**, click
   **"Save"**, then **"Test"**.

**Free offer and limits**

- Models whose name ends with `:free` cost nothing. Echo shows them with the **Free** tag.
- Free models: **20 requests per minute**, and **50 requests per day** if you bought less than
  10 USD of credits, or **1,000 per day** from 10 USD of credits.

**Privacy**

Some free models run on providers that may keep or use your prompts. You choose what you accept in
[openrouter.ai/settings/privacy](https://openrouter.ai/settings/privacy).

**Common errors**

| Error | Meaning | What to do |
|---|---|---|
| 503 or "No endpoints found matching your data policy" | Your privacy settings block every endpoint of this free model | In openrouter.ai/settings/privacy, allow free endpoints that may train on data, or pick another model. |
| 402 | Negative balance (even for `:free` models) | Add credits, or use another provider. |
| 429 | Per-minute or per-day quota reached | Wait. Echo retries, then falls back to the other provider. |

<a id="troubleshooting"></a>

## Troubleshooting

### Common issues

- **Transcription fails.** Check the key (Settings > Providers > **"Test"**) and your quota with the
  provider. The details are in `echo.log`, in `%LOCALAPPDATA%\Echo`.
- **Saturated (429).** The key hit its limit or the service is busy. Echo retries
  for about two minutes, switches to the other provider if its key is set, then marks the meeting
  "Waiting for API" and retries after 10, 30 and 60 minutes. A transcript already produced is never
  lost.
- **"Model unavailable on this key's tier" (403).** Your key cannot use that model. In
  Settings > Providers, turn on "Show model details" and pick a model with the **Free** tag, or
  switch to another provider.
- **The red "API key missing · Help" button** in the sidebar. A key needed by the chosen engines is
  missing, or a call failed. Click it to open this help.
- **Which engine is really used?** The choice is made in one place: Settings > Providers. The
  Transcription and Minutes pages show the engine and the model in use.
- **Disk space.** Settings > Storage shows what your recordings weigh. One hour of meeting weighs
  about 1 GB while recording, then the audio is archived to MP3. **"Clean up"** applies the
  retention rules now. Transcripts, minutes, notes and the listening MP3 are never deleted.
- **"A recording is already running".** One recording at a time. If the widget is gone, use the
  keyboard shortcut or the icon near the clock to stop.
- **"Interrupted recording(s) recovered".** At startup, Echo repairs meetings that were not stopped
  cleanly (PC shut down, app closed). They show in the list, ready to transcribe.
- **No speaker names.** Echo sets the Teams option it needs on first launch. Then **fully quit Teams
  once** (right-click the Teams icon > Quit) and open it again.
- **Attendees not detected (compact view).** When the meeting is shrunk to the **small floating
  tile**, Teams shows neither attendees nor who is speaking. Keep the **meeting window large**, at
  least from time to time. Echo warns you and picks the names up when you enlarge it.
- **"Participants' audio unavailable".** The meeting sound must come out of **this PC**. Recording
  goes on with the microphone only.
- **Your voice is missing.** Check that the right **microphone** was Windows' default device, and
  that Teams was not using it exclusively. The audio is kept: "Re-run transcription" tries again.
- **The widget disappeared.** It was hidden or folded. The **Widget** button of the home banner
  brings it back during a recording.
- **The window disappeared.** Echo still runs in the **notification area** (the "show hidden icons"
  arrow). Click its icon.
- **Updates.** Echo checks GitHub for a new version at startup and offers it (Settings > General >
  "Check on startup"). **"Check now"** checks right away. If something goes wrong after an update,
  **"Previous version"** restores the old install.

### Output files

Each meeting has its own folder in `%LOCALAPPDATA%\Echo\recordings` ("Open local folder" button).

| File | Content |
|---|---|
| **&lt;Title&gt; - Minutes.eml / .md / .docx** | The minutes, ready to send from Outlook ("Compte rendu" when the app is in French) |
| **&lt;Title&gt; - Transcript.txt** | The transcription by speaker, corrected |
| **&lt;Title&gt; - Summary.md** | The short summary ("Résumé" in French) |
| **&lt;Title&gt; - Audio.mp3** | The audio recording |
| **notes.md** | Your time-stamped notes and key moments |
| **Captures/** | The screenshots |
| **.echo/** (hidden) | Echo's working files: archived raw tracks (mic.mp3 / system.mp3, used to transcribe again), raw transcript, meeting info, logs |

&lt;Title&gt; is the meeting name: renaming the meeting renames these files. Older meetings are tidied
into this layout the first time Echo starts.

After transcription, the raw audio (WAV, about 1 GB per hour) is archived to one MP3 per track,
about 20 times smaller. Transcribing again still works. "Keep original WAVs" turns this off, and
"Audio deletion (days)" removes the audio of transcribed meetings after N days.

<a id="settings"></a>

## Every setting at a glance

| Settings > | What you find there |
|---|---|
| **Account** | Your name, which signs the minutes. |
| **Recording** | Meeting detection, automatic recording, automatic screenshots, screenshot area (Teams window or whole screen), global keyboard shortcut (`Ctrl+Alt+R` by default, can be changed or turned off). |
| **Transcription** | The engine in use (chosen in Providers), the meeting language (Auto, French or English), automatic transcription at the end of the meeting, vocabulary correction (LLM). |
| **Vocabulary** | Your business terms and corrections, general or per mission, file import, the "Copy-paste prompt" button. |
| **Minutes** | The engine in use, automatic minutes, minutes language, formats (.eml, .md, .docx), default template, your own templates. |
| **Providers** | Transcription engine, minutes & summary engine, automatic fallback, one key per provider with "Save" and "Test", the Cloudflare account id, "Show model details" (the model for each task, with the **Free** tag), Teams transcript import. |
| **Storage** | Disk use, audio deletion after N days, failed meetings (days), keep original WAVs, "Clean up now". |
| **General** | Theme, application language (French or English), launch at Windows startup, recordings folder, updates ("Check on startup", "Check now", "Previous version"), help and version. |
