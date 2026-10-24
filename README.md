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
5. Converts images for real (PNG, JPG, BMP, GIF, TIFF). Video, audio and documents show
   their wheels but only log the choice for now.

The converted file is saved as a new copy next to the original (`photo.png`, or `photo (1).png`
if that name is taken). The original is never touched: the wheel always answers the drag with
"Copy", never "Move".

### Run it

Needs the .NET 10 SDK.

```bash
dotnet run --project prototype/GestureSpike
```

A tray icon appears with a "GestureSpike is running" notification. Right-click the tray icon for
**Pause gesture**, **Open log** and **Exit**.

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

### How it's put together

| File | What it does |
| --- | --- |
| `MouseHook.cs` | Low-level hook on its own thread; detects the chord and swallows the right-click. |
| `WheelWindow.xaml(.cs)` | Transparent, non-activating, topmost wheel; it's the drop target and draws the wedges. |
| `Formats.cs` | Which output formats to offer for the dropped file types. |
| `ImageConverter.cs` | Image conversion via Windows Imaging Component, including EXIF rotation. |
| `App.xaml.cs` | Tray icon, allowlist / full-screen checks, wiring, running conversions off the UI thread. |
