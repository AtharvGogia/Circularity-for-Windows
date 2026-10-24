# File Converter for Windows

A Tangerine-style file converter for Windows: drag a file, tap right-click (or Shift) mid-drag,
and drop it on a button in a wheel that pops up around your cursor.

Right now this repo holds a working **prototype**, `prototype/GestureSpike`. Packaging it as a
shippable app comes later.

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
4. Runs the job in the background:
   - **Images** → PNG, JPG, BMP, GIF, TIFF (Windows Imaging Component)
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
   - Documents: not yet (the wheel says "Documents coming soon").

Results are saved as new copies next to the original: `photo.png`, `photo (resized).jpg`,
`photo (cropped).jpg`, `photo (compressed).jpg`, with ` (1)`, ` (2)`... if the name is taken. The original is never touched:
the wheel always answers the drag with "Copy", never "Move".

While something is running, hover the tray icon to see progress
(`clip.mov → MP4 43% (+2 more)`). **Cancel conversions** in its menu stops everything. A cancelled or
failed job leaves no half-written file behind. You get one notification per drop.

### Settings

Double-click the tray icon (or right-click → **Settings…**) for:

- wheel **size** (200–360 px) and **background opacity**
- **accent colour**: presets or any custom colour; the tray icon follows it
- which **gestures** are on (right-click, Shift)

Changes apply live, with a preview, and are saved to `%APPDATA%\GestureSpike\settings.json`.

## Run it

Needs the .NET 10 SDK, and FFmpeg for video/audio:

```bash
winget install Gyan.FFmpeg
```

The app looks for `ffmpeg.exe` in `FFMPEG_PATH`, next to the app, in a `tools\` folder beside it,
then on `PATH`.

```bash
dotnet run --project prototype/GestureSpike
```

A tray icon appears with a "GestureSpike is running" notification. Right-click it for
**Settings…**, **Pause gestures**, **Cancel conversions**, **Open log** and **Exit**.

## Test it

A console harness generates sample video, audio and image files with FFmpeg, runs every supported
conversion, resize, crop and compression through the real queue, and checks the outputs decode
cleanly, have the right pixel sizes, and fit under the requested file size. It also checks naming, unsupported formats, batches, progress and cancellation.

```bash
dotnet run --project prototype/EngineSmokeTest
```

It can also render the wheel, the settings window and the resize / compress / crop dialogs to
PNGs, to check the look:

```bash
dotnet run --project prototype/EngineSmokeTest -- --render-previews previews
```

And it can do a real drag-and-drop onto the wheel with synthetic mouse input, checking that
dropping on a button picks it and that dragging off dismisses the wheel. **It moves your mouse for
a few seconds**, so keep your hands off it while it runs:

```bash
dotnet run --project prototype/EngineSmokeTest -- --drag-test
```

Everything the hooks decide is written to `%LOCALAPPDATA%\GestureSpike\log.txt`.

## Known limits

- The gestures only arm when the app you drag from is on an allowlist (Explorer and the main
  browsers and mail apps, see `App.xaml.cs`) and nothing full-screen is running.
- Dragging from an app running as administrator won't drop onto the wheel; Windows blocks
  drops from elevated apps into normal ones.
- HEIC and WebP input need Microsoft's free HEIF / WebP image extensions installed. They can't
  be written, so resizing or cropping them produces a JPG.
- Animated GIFs are edited as their first frame only.
- Licensing, before shipping: the Gyan FFmpeg build is GPL because it includes x264. Bundling it
  means the app must be GPL too; a closed-source app needs an LGPL FFmpeg build and a different
  H.264 encoder (e.g. OpenH264 or the GPU's hardware encoder).

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
| `Engine/OutputPaths.cs` | Picks a free output name next to the original and reserves it. |
| `App.xaml.cs` | Tray icon and menu, gesture checks, routing wheel buttons to dialogs and the queue, notifications. |
