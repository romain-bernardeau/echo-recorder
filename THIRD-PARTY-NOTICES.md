# Third-party components

Echo is licensed under the MIT License (see `LICENSE`). It uses or ships the following components,
each under its own license.

## Bundled in the installer and the release zip

**FFmpeg 9.0.2 with LAME 3.100** (`bin/ffmpeg.exe`), used to decode, resample, mix and encode audio.
- License: GNU LGPL v2.1 or later (FFmpeg), GNU LGPL v2 or later (LAME). This is an audio-only
  build made for Echo, without any GPL or nonfree component and without video codecs.
- License texts next to the binary: `bin/FFMPEG-COPYING.LGPLv2.1.txt`, `bin/FFMPEG-LICENSE.md`,
  `bin/LAME-COPYING.txt`; versions, checksums and build options in `bin/BUILD-INFO.txt`.
- Complete corresponding source: the `ffmpeg-audio-9.0.2` release of this repository holds the
  exact FFmpeg and LAME source archives and the build script
  ([tools/ffmpeg-audio/build.sh](tools/ffmpeg-audio/build.sh)), built by the
  [ffmpeg-audio workflow](.github/workflows/ffmpeg-audio.yml). You may rebuild it, modify it and
  replace `bin/ffmpeg.exe` with your own build.
- `bin/ffmpeg.exe` is a separate program called by Echo, not linked into Echo, so Echo's own code
  stays under MIT.

**.NET 8 runtime, WPF and Windows Forms** (self-contained publish). License: MIT.

## NuGet packages

| Package | License |
|---|---|
| NAudio | MIT |
| FlaUI.UIA3, FlaUI.Core, Interop.UIAutomationClient | MIT |
| DocumentFormat.OpenXml, DocumentFormat.OpenXml.Framework, System.IO.Packaging | MIT |
| System.Security.Cryptography.ProtectedData | MIT |
| Microsoft.Web.WebView2 | BSD 3-Clause (Microsoft) |
| xunit, Microsoft.NET.Test.Sdk, coverlet.collector (tests only) | Apache 2.0 / MIT |

## Ported code

`src/Echo.Ai/Difflib.cs` is a port of the `SequenceMatcher` algorithm of Python's `difflib`
module. Python is distributed under the PSF License Agreement
(https://docs.python.org/3/license.html), which is GPL-compatible.
Copyright (c) 2001-2024 Python Software Foundation; All Rights Reserved.
