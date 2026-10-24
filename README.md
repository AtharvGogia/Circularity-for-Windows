# File Converter for Windows

A Tangerine-style file converter for Windows: drag a file, tap right-click mid-drag,
and drop it on a format in a wheel that pops up around your cursor.

Right now this repo holds one thing: a **prototype** that tests whether that gesture works.

## The gesture prototype (`prototype/GestureSpike`)

A tray app that:

1. Installs a system-wide low-level mouse hook.
2. Watches for the chord: **left button held and dragged**, then **right button pressed**.
3. Swallows that right-click, so the app you're dragging from doesn't cancel the drag.
4. Pops up a wheel of formats around the cursor. The wheel is a real drop target, so you
   keep dragging onto a format and release.
5. Queues the conversion and runs it in the background:
   - **Images** → PNG, JPG, BMP, GIF, TIFF (Windows Imaging Component)
   - **Video** → MP4, WebM, MOV, GIF, MP3 (FFmpeg)
   - **Audio** → MP3, WAV, FLAC, OGG, M4A (FFmpeg)
   - Documents show their wheel but aren't converted yet; you get a "not supported yet" notice.

While something is converting, hover the tray icon to see progress
(`clip.mov → MP4 43% (+2 more)`), and use **Cancel conversions** in its menu to stop everything.
A cancelled or failed conversion leaves no half-written file behind. One notification arrives per
drop, when every file in it is done.

The converted file is saved as a new copy next to the original (`photo.png`, or `photo (1).png`
if that name is taken). The original is never touched: the wheel always answers the drag with
"Copy", never "Move".

### Run it

Needs the .NET 10 SDK, and FFmpeg for video/audio:

```bash
winget install Gyan.FFmpeg
```

The app looks for `ffmpeg.exe` in `FFMPEG_PATH`, next to the app, in a `tools\` folder beside it,
then on `PATH`.

```bash
dotnet run --project prototype/GestureSpike
```

A tray icon appears with a "GestureSpike is running" notification. Right-click the tray icon for
**Pause gesture**, **Cancel conversions**, **Open log** and **Exit**.

### Test the engine

A console harness generates sample video, audio and image files with FFmpeg, runs every
supported conversion through the real queue, and checks the outputs decode cleanly. It also
checks naming, unsupported formats, batches, progress and cancellation.

```bash
dotnet run --project prototype/EngineSmokeTest
```

### Try it

1. In File Explorer (or on the desktop), start dragging an image with the left button.
2. While still holding left, tap right-click once. The wheel appears centred on the cursor.
3. Keep holding left and move onto a format; it lights up orange.
4. Release. A notification says what was created.

Releasing in the centre of the wheel, or pressing Esc, cancels.

### Things to check (what the prototype is for)

- [ ] Explorer drag survives the swallowed right-click (doesn't cancel, doesn't show its menu).
- [ ] Same from the desktop, and from a browser's downloads list.
- [ ] The wheel appears in the right place on every monitor, including mixed-DPI setups.
- [ ] Dropping never moves or deletes the original.
- [ ] Left+right in a game or other app outside the allowlist is left alone.
- [ ] How it feels on a laptop touchpad (expected: awkward; needs a keyboard fallback later).

Everything the hook decides is written to `%LOCALAPPDATA%\GestureSpike\log.txt`.

### Known limits

- The gesture only arms when the app you drag from is on an allowlist (Explorer and the main
  browsers and mail apps, see `App.xaml.cs`) and nothing full-screen is running.
- Dragging from an app running as administrator won't drop onto the wheel; Windows blocks
  drops from elevated apps into normal ones.
- HEIC and WebP input need Microsoft's free HEIF / WebP image extensions installed.
  WebP output isn't supported.
- Licensing, before shipping: the Gyan FFmpeg build is GPL because it includes x264. Bundling it
  means the app must be GPL too; a closed-source app needs an LGPL FFmpeg build and a different
  H.264 encoder (e.g. OpenH264 or the GPU's hardware encoder).

### How it's put together

| File | What it does |
| --- | --- |
| `MouseHook.cs` | Low-level hook on its own thread; detects the chord and swallows the right-click. |
| `WheelWindow.xaml(.cs)` | Transparent, non-activating, topmost wheel; it's the drop target and draws the wedges. |
| `Formats.cs` | Which output formats to offer for the dropped file types. |
| `Engine/ConversionQueue.cs` | Background job queue (two at a time): progress, cancellation, per-drop batches, cleanup of failed output. |
| `Engine/IConverter.cs` | The interface each conversion backend implements. |
| `Engine/ImageConverter.cs` | Image conversion via Windows Imaging Component, including EXIF rotation. |
| `Engine/FfmpegConverter.cs` | Runs `ffmpeg.exe` with per-format settings and parses its progress output. |
| `Engine/OutputPaths.cs` | Picks a free `name (n).ext` next to the original and reserves it. |
| `App.xaml.cs` | Tray icon and menu, allowlist / full-screen checks, progress tooltip, notifications. |
