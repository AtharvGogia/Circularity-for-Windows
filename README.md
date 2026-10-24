# Circularity

A file converter for Windows that lives at your cursor: drag a file, tap right-click (or Shift)
mid-drag, and drop it on a button in the wheel that pops up around it.

Made by **Atharv Gogia**.

The app is in `src/Circularity`. Packaging it as an installable app comes later.

## What it does

A tray app that:

1. Watches for a file drag (left button held and moved), using system-wide low-level hooks.
2. Opens the wheel when, mid-drag, you either:
   - **press the right mouse button**, or
   - **press Shift**. This is the fallback for touchpads, where pressing two buttons while
     dragging is awkward. If Shift was already held when the drag started, the wheel stays out
     of the way, so Explorer's own Shift-to-move still works.

   The right-click or Shift press is swallowed, so the app you're dragging from never sees it.
3. Shows a translucent wheel around the cursor, its ring split into curved, icon-labelled segments. The wheel is a
   real drop target: keep dragging onto a button and release. Dropping in the middle cancels;
   dragging off the wheel dismisses it, and the drag carries on as normal.

   **Without dragging**, the same wheel opens at the cursor in click mode: hover and click a button,
   or use the arrow keys and Enter; Esc, the centre or clicking elsewhere cancels. Three ways in:
   - **Ctrl+Alt+C** with files selected in File Explorer (any tab) or on the desktop
   - **Right-click → Convert with Circularity** (added by the installer; on Windows 11 it's under
     "Show more options")
   - **Right-click → Send to → Circularity**, which hands over every selected file at once

   Launching Circularity while it's already running hands the files to the running copy (or, with no
   files, opens Settings), so these never start a second copy.
4. Runs the job in the background:
   - **Images** → PNG, JPG, BMP, GIF, TIFF (Windows Imaging Component), WebP (decoded by Windows,
     encoded by FFmpeg's libwebp), PDF
   - **Video** → MP4, WebM, MOV, GIF, MP3 (FFmpeg)
   - **Audio** → MP3, WAV, FLAC, OGG, M4A (FFmpeg)
   - **Resize** (images and video): to a width × height, keeping the aspect ratio (fit inside) or
     not (stretch). Opens a small dialog that shows the resulting size.
   - **Compress** (images): by **quality** percentage, or to a **target file size** (e.g. 500 KB),
     where it keeps the highest quality that fits and scales the picture down only if even low
     quality is too big. Saved as JPG (transparent areas become white); the notification shows the
     size before and after.
   - **Crop** (images and video): opens an editor with a crop box you can move, resize from any
     corner or edge, or redraw, optionally locked to 1:1, 4:3, 3:2, 16:9 or 9:16. Videos are
     shown as a frame from about one second in. Several files are cropped one after another.
   - **Documents**, with nothing extra to install:
     - **PDF** → PNG / JPG (one image per page; several pages go into a `name (pages)` folder) using
       Windows' own PDF renderer, and → TXT using PdfPig
     - **Markdown** → PDF or HTML, **HTML** → PDF, **TXT** → PDF, laid out by Edge (WebView2), on
       A4 or US Letter depending on your region
   - **Documents through Microsoft Office**, when it's installed (otherwise these buttons don't appear):
     - **Word**: DOCX / DOC / RTF / ODT → PDF, DOCX, TXT
     - **Excel**: XLSX / XLS / ODS / CSV → PDF
     - **PowerPoint**: PPTX / PPT / ODP → PDF

     Office runs invisibly. If you already had that Office app open, your copy is used and left
     open. A password-protected file fails straight away instead of asking for a password, and an
     Office job that hangs is stopped after 3 minutes.
   - **Combine** (two or more images and/or PDFs): one PDF with every page, in the order dropped.

Results are saved as new copies next to the original: `photo.png`, `photo (resized).jpg`,
`photo (cropped).jpg`, `photo (compressed).jpg`, `scan (combined).pdf`, with ` (1)`, ` (2)`... if the name is taken. The original is never touched:
the wheel always answers the drag with "Copy", never "Move".

While something is running, hover the tray icon to see progress
(`clip.mov → MP4 43% (+2 more)`). **Cancel conversions** in its menu stops everything. A cancelled or
failed job leaves no half-written file behind. You get one notification per drop; clicking it shows
the result in File Explorer.

The first time Circularity starts, a short welcome explains the gestures.

### Settings

Double-click the tray icon (or right-click → **Settings…**) for:

- **theme**: same as Windows (follows it live), light or dark, for the wheel and every window
- wheel **size** (200–360 px) and **background opacity**
- **accent colour**: presets or any custom colour; the tray icon follows it
- which **gestures** are on (right-click, Shift, the Ctrl+Alt+C shortcut)
- **notifications** when conversions finish and when Circularity starts (problems always show)
- **start at sign-in**: the same Windows sign-in entry the installer's checkbox creates, so the two
  always agree; it also notices (and, when ticked, undoes) "Disabled" in Task Manager's Startup apps

Changes apply live, with a preview, and are saved to `%APPDATA%\Circularity\settings.json`.

## Build the installer

One command builds everything users need, with .NET and FFmpeg included:

```bash
powershell -ExecutionPolicy Bypass -File build\build.ps1
```

It produces, in `artifacts\`:

| Output | What it is | Size |
| --- | --- | --- |
| `artifacts\CircularitySetup-0.2.0.exe` | The installer to hand out | ~64 MB |
| `artifacts\Circularity\Circularity.exe` | The app itself, in a self-contained folder that also runs without installing (e.g. from a USB stick) | ~214 MB folder |

The installer:

- installs for the current user, so there's no admin prompt, into
  `%LOCALAPPDATA%\Programs\Circularity\Circularity.exe`
- adds a Start menu shortcut and a **Send to** entry, and optionally **Convert with Circularity**
  on files' right-click menu (on by default), starting at sign-in, and a desktop shortcut
- installs Microsoft's WebView2 only on the rare PC that doesn't have it (Windows 11 always does)
- appears in **Settings → Apps** with a normal uninstaller, which also removes the sign-in entry

It isn't code-signed, so Windows SmartScreen shows "Windows protected your PC" the first time;
users click **More info → Run anyway**.

One-time setup for building it:

```bash
winget install Microsoft.DotNet.SDK.10
```

```bash
winget install JRSoftware.InnoSetup
```

```bash
winget install MSYS2.MSYS2
```

MSYS2 is only needed to compile FFmpeg, which happens once (a few minutes) and is reused after
that; `build\build.ps1 -RebuildFfmpeg` forces a fresh one. That FFmpeg (`build\ffmpeg\build-ffmpeg.sh`)
is a slim LGPL build, 29 MB instead of the usual 230 MB: only the encoders, containers and filters
Circularity uses, every decoder so it still reads almost anything, and H.264 encoding through
Windows' built-in encoder instead of the GPL x264. It needs only DLLs that come with Windows.
Licences for everything bundled are in `build\installer` and ship in the app's `licenses` folder,
listed in `THIRD-PARTY-NOTICES.txt`.

## Run it for development

Needs the .NET 10 SDK, and FFmpeg for video/audio:

```bash
winget install Gyan.FFmpeg
```

The app looks for `ffmpeg.exe` in `FFMPEG_PATH`, next to the app, in a `tools\` folder beside it,
then on `PATH`.

```bash
dotnet run --project src/Circularity
```

That development build lands in `src\Circularity\bin\Debug\net10.0-windows10.0.19041.0\Circularity.exe`
and needs .NET 10 installed; the installer above is what to give other people.

A tray icon appears with a "Circularity is running" notification. Right-click it for
**Settings…**, **Pause gestures**, **Cancel conversions**, **Open log** and **Exit**.

## Test it

A console harness generates sample video, audio and image files with FFmpeg, runs every supported
conversion, resize, crop and compression through the real queue, and checks the outputs decode
cleanly, have the right pixel sizes, and fit under the requested file size. It also checks naming,
unsupported formats, batches, progress and cancellation.

```bash
dotnet run --project src/EngineSmokeTest
```

To test against the slim FFmpeg that ships in the installer, point `FFMPEG_PATH` at it first
(`$env:FFMPEG_PATH = "build\ffmpeg\out\bin\ffmpeg.exe"` in PowerShell).

It can also render the wheel, the settings window and the resize / compress / crop dialogs to
PNGs, to check the look:

```bash
dotnet run --project src/EngineSmokeTest -- --render-previews previews
```

And it can do a real drag-and-drop onto the wheel with synthetic mouse input, checking that
dropping on a button picks it and that dragging off dismisses the wheel. **It moves your mouse for
a few seconds**, so keep your hands off it while it runs:

```bash
dotnet run --project src/EngineSmokeTest -- --drag-test
```

`--interaction-test` checks the no-drag ways in: the hand-off between launches, the Ctrl+Alt+C
registration, click mode by keyboard, and reading the selection from a real File Explorer window
(an Explorer window and the wheel flash up briefly; run it with Circularity closed).

`--startup-test` checks the start-at-sign-in toggle against the real registry, then restores
whatever was there before.

Everything the hooks decide is written to `%LOCALAPPDATA%\Circularity\log.txt`.

## Known limits

- The gestures only arm when the app you drag from is on an allowlist (Explorer and the main
  browsers and mail apps, see `App.xaml.cs`) and nothing full-screen is running.
- Dragging from an app running as administrator won't drop onto the wheel; Windows blocks
  drops from elevated apps into normal ones.
- HEIC and WebP input need Microsoft's free HEIF / WebP image extensions installed. They can't
  be written, so resizing or cropping them produces a JPG.
- Animated GIFs are edited as their first frame only.
- Licensing: the FFmpeg bundled in the installer is LGPL (see above), so Circularity can stay
  closed-source. The Gyan "full" FFmpeg suggested for development is GPL; don't ship that one.
- The other libraries are fine for a closed-source app: PdfPig (Apache-2.0), Markdig (BSD-2-Clause),
  WebView2 (Microsoft's redistributable licence). PDFsharp was deliberately not used: from 6.5 it's
  AGPL, which would force the whole app to be open-source.
- PDF → DOCX isn't offered. Word can do it, but first shows a "Word will now convert your PDF"
  prompt that an invisible Word can't answer. Silencing it means changing a Word option.
- Markdown/HTML/TXT → PDF needs the WebView2 runtime. It's part of Windows 11 and most up-to-date
  Windows 10 PCs; without it those buttons don't appear.

## How it's put together

| File | What it does |
| --- | --- |
| `GestureHook.cs` | Mouse + keyboard hooks on their own thread; detects right-click or Shift mid-drag and swallows it. |
| `WheelWindow.xaml(.cs)` | Transparent, never-focused, topmost window; the drop target; dismisses when dragged off. |
| `WheelView.cs` | Draws the wheel (also used for the live preview in Settings). |
| `Formats.cs` | Which buttons (formats and tools) to offer for the dropped files, and their icons. |
| `SettingsWindow.xaml(.cs)` / `Settings.cs` | Settings UI with live preview / the saved settings. |
| `ResizeDialog.xaml(.cs)` | Resize options. |
| `CompressDialog.xaml(.cs)` | Compression options (quality or target size). |
| `CropWindow.xaml(.cs)` | Crop editor. |
| `AppIcon.cs` | Draws the accent-coloured app icon at runtime. |
| `Engine/ConversionQueue.cs` | Background job queue (two at a time): progress, cancellation, per-drop batches, cleanup of failed output. |
| `Engine/Operations.cs` | Convert / resize / crop / compress, and how a resize maps a source size to a new one. |
| `Engine/IConverter.cs` | The interface each backend implements. |
| `Engine/ImageConverter.cs` | Images via Windows Imaging Component, including EXIF rotation. |
| `Engine/FfmpegConverter.cs` | Runs `ffmpeg.exe` for video/audio; reads video sizes, grabs preview frames, parses progress. |
| `Engine/PdfConverter.cs` | PDF → images (Windows.Data.Pdf) and text (PdfPig); images/PDFs → one PDF (PdfPig). |
| `Engine/WebPdfConverter.cs` | Markdown / HTML / TXT → PDF in a hidden WebView2; Markdown → HTML (Markdig). |
| `Engine/OfficeConverter.cs` | Word / Excel / PowerPoint automation, with the hang and password safeguards. |
| `Engine/Sta.cs` | Runs work on the single-threaded apartment thread WPF imaging and Office need. |
| `Engine/OutputPaths.cs` | Picks a free output name (or folder) next to the original and reserves it. |
| `Theme.cs` | Light / dark / same-as-Windows, for the wheel and the windows. |
| `InstanceChannel.cs` | One running copy; later launches hand their files over a private named pipe. |
| `GlobalHotkey.cs` / `ExplorerSelection.cs` | Ctrl+Alt+C, and reading the selected files from Explorer or the desktop. |
| `WelcomeWindow.xaml(.cs)` | The first-run welcome. |
| `StartAtSignIn.cs` | The start-at-sign-in toggle (shared with the installer). |
| `App.xaml.cs` | Tray icon and menu, gesture checks, routing wheel buttons to dialogs and the queue, notifications. |
