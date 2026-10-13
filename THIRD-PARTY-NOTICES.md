# Third-party components

Echo is licensed under the MIT License (see `LICENSE`). It uses or ships the following components,
each under its own license.

## Bundled in the release zip

**FFmpeg** (`bin/ffmpeg.exe`), used to convert and encode audio.
- License: GNU GPL v3 (the "essentials" build from https://www.gyan.dev/ffmpeg/builds/).
- Source code: https://ffmpeg.org/download.html and https://github.com/FFmpeg/FFmpeg
- `bin/ffmpeg.exe` is a separate program called by Echo (it is not linked into Echo, so Echo's own
  code stays under MIT); its exact version is printed by `ffmpeg -version`.

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
