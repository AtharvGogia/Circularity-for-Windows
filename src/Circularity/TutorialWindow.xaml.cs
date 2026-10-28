using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Documents;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;

namespace Circularity;

/// <summary>
/// The full tutorial: a chapter list on the left and one page at a time on the right, some with a
/// wheel to look at or try. Much longer than the first-run welcome, and opened from Help.
/// </summary>
internal partial class TutorialWindow : Window
{
    private sealed record Chapter(string Title, Func<FrameworkElement> Build);

    private readonly Settings _settings;
    private readonly Func<string, Engine.Operation, bool> _canHandle;
    private readonly List<Chapter> _chapters;
    private readonly Brush _accent;
    private readonly Brush _tint;

    // Wheels cycling on the current page, stopped when it's left.
    private readonly List<DispatcherTimer> _timers = new();

    /// <summary>Raised by the "Open settings" buttons in the tutorial.</summary>
    public event Action? OpenSettingsRequested;

    public TutorialWindow(Settings settings, Func<string, Engine.Operation, bool> canHandle)
    {
        InitializeComponent();
        _settings = settings.Clone();
        _canHandle = canHandle;
        var accent = _settings.Accent;
        _accent = new SolidColorBrush(accent);
        _tint = new SolidColorBrush(Color.FromArgb(0x26, accent.R, accent.G, accent.B));

        _chapters = new()
        {
            new("Welcome", Welcome),
            new("Open the wheel by dragging", Dragging),
            new("Drop on a button", Dropping),
            new("Try the wheel", TryIt),
            new("Without dragging", WithoutDragging),
            new("What it converts", Formats_),
            new("Resize and crop", ResizeAndCrop),
            new("Compress", Compress),
            new("Trim and combine", TrimAndCombine),
            new("Your own buttons", OwnButtons),
            new("After converting", AfterConverting),
            new("Settings and the tray", SettingsAndTray),
            new("If something isn't working", Troubleshooting),
        };

        for (int i = 0; i < _chapters.Count; i++)
        {
            // The numbers in a column of their own, so 10 and up line up with 1 to 9.
            var row = new Grid();
            row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(26) });
            row.ColumnDefinitions.Add(new ColumnDefinition());
            var title = new TextBlock { Text = _chapters[i].Title, TextWrapping = TextWrapping.Wrap };
            Grid.SetColumn(title, 1);
            row.Children.Add(new TextBlock { Text = $"{i + 1}.", Opacity = 0.6 });
            row.Children.Add(title);
            Chapters.Items.Add(new ListBoxItem { Content = row, Padding = new Thickness(10, 7, 10, 7) });
        }
        Chapters.SelectionChanged += (_, _) => ShowChapter(Chapters.SelectedIndex);
        Chapters.SelectedIndex = 0;

        Loaded += (_, _) => WindowHelpers.ForceForeground(this);
        Closed += (_, _) => StopTimers();
    }

    private void ShowChapter(int index)
    {
        if (index < 0)
            return;
        StopTimers();
        PageHost.Content = _chapters[index].Build();
        PageScroll.ScrollToTop();
        PageText.Text = $"{index + 1} of {_chapters.Count}";
        BackButton.IsEnabled = index > 0;
        NextButton.Content = index == _chapters.Count - 1 ? "Done" : "Next";
    }

    private void StopTimers()
    {
        foreach (var timer in _timers)
            timer.Stop();
        _timers.Clear();
    }

    private void Next_Click(object sender, RoutedEventArgs e)
    {
        if (Chapters.SelectedIndex >= _chapters.Count - 1)
            Close();
        else
            Chapters.SelectedIndex++;
    }

    private void Back_Click(object sender, RoutedEventArgs e)
    {
        if (Chapters.SelectedIndex > 0)
            Chapters.SelectedIndex--;
    }

    // --- Chapters ---

    private FrameworkElement Welcome() => Page("Welcome to Circularity",
        Beside(CyclingWheel("photo.jpg", "3.4 MB"),
            Para("Circularity converts files right where your cursor is. Instead of opening an app, picking a file and hunting through menus, you drag the file as you normally would, tap **right-click**, and drop it on a button in the wheel that appears around the cursor."),
            Para("It lives in the **tray**, by the clock (click the **^** arrow if you can't see it). It uses almost nothing while it waits."),
            Para("Everything happens on your PC: **nothing is uploaded**, and the **original file is never changed**. Converted files are saved as new copies next to the original.")),
        Heading("How this tutorial works"),
        Para("Each chapter covers one part of Circularity, from the basics to the details. Read them in order with **Next**, or jump to any chapter on the left. Chapter 4 has a wheel you can try with the mouse, and chapter 2 can make a practice file for you."),
        Tip("Just want the short version?", "Drag a file, tap right-click while still holding the left button, and let go on a format. That's it."));

    private FrameworkElement Dragging() => Page("Open the wheel by dragging",
        Para("This is the main way to use Circularity, and it works from **File Explorer**, the **desktop**, and files dragged out of the main **browsers and mail apps** (Edge, Chrome, Firefox, Brave, Opera, Vivaldi, Outlook and Thunderbird)."),
        Steps(
            "Press the **left** mouse button on a file and start dragging it, as if you were moving it to another folder.",
            "While you're still holding the left button, **tap the right mouse button** once (press and let go).",
            "The wheel appears around the cursor. Keep holding the left button and move onto a button.",
            "Let go of the left button. The conversion starts in the background."),
        Heading("On a touchpad"),
        Para("Pressing two buttons while dragging is awkward on a touchpad, so you can **press Shift** instead of right-clicking. Start the drag first and then press Shift: if Shift is already held when the drag starts, the wheel stays out of the way so Explorer's own **Shift to move** still works."),
        Para("The right-click or Shift press is kept from the app you're dragging from, so it never opens a menu there or changes what the drag does."),
        Heading("Practice"),
        Para("Make a picture to practise on. It's saved in a folder of its own, and File Explorer opens with it selected, ready to drag."),
        PracticeButton(),
        Tip("The wheel didn't appear?", "It only opens for drags from the apps above, not while something is running full screen (a game or video), and not when gestures are paused from the tray menu. Chapter 13 has more."));

    private FrameworkElement Dropping() => Page("Drop on a button",
        Para("Once the wheel is open, it works like any other place you can drop a file: move onto a button and let go."),
        Bullets(
            "**A button** converts the file, or opens a small window first for tools like Resize, Crop, Compress and Trim.",
            "**The middle** cancels. While you're over it, it says Cancel; when you're not, it shows the file's name and size.",
            "**Dragging off the wheel** closes it, and the drag carries on as normal, so you can still drop the file somewhere else."),
        Para("As you move over the buttons, the label under the dragged file says what letting go will do, for example **Convert to PNG** or **Cancel**. (Windows draws that label for drags from File Explorer and the desktop.)"),
        Heading("Your original is safe"),
        Para("The wheel always answers a drag with **Copy**, never Move, so the original stays exactly where it was. The new file is saved next to it: converting **photo.jpg** to PNG gives **photo.png**, and if that name is taken, **photo (1).png**."),
        Heading("Several files at once"),
        Para("Select several files and drag them together. The wheel shows only the buttons that make sense for all of them, and you get one notification when they're all done. Two or more images or PDFs also get a **Combine** button, which puts them all in one PDF."));

    private FrameworkElement TryIt() => Page("Try the wheel",
        Para("This wheel works like the real one: move the mouse over it to light up buttons. Rest on **More** to open the arc of extra buttons. Clicking a button here only flashes it; nothing is converted."),
        LiveWheel(),
        Heading("How the wheel is laid out"),
        Bullets(
            "The ring holds **at most six buttons**, so each one stays big enough to hit without looking.",
            "When a file has more to offer, the bottom button becomes **More**. Resting on it fans the rest out in an arc just outside the wheel, which you drop onto the same way.",
            "Everyday formats sit at the **top and on the right**. Buttons that open a window first (Resize, Compress, Crop, Trim) always sit to the **left of More**, on the ring or at the left end of the arc.",
            "Near the bottom of the screen, where the arc wouldn't fit, More moves to the **top** and the arc opens upwards.",
            "The buttons depend on the file: try Video, Audio and Document above."),
        Tip("Change the order", "Settings → Buttons lets you move buttons up (onto the ring) or down (behind More), and hide ones you never use. Chapter 10 has the details."));

    private FrameworkElement WithoutDragging() => Page("Without dragging",
        Para("You don't have to drag. These open the same wheel at the cursor, in **click mode**:"),
        Bullets(
            "**Ctrl+Alt+C**: select files in File Explorer (any tab) or on the desktop, then press Ctrl+Alt+C.",
            "**Right-click a file → Convert with Circularity.** On Windows 11 it's under **Show more options**.",
            "**Right-click → Send to → Circularity**, which hands over every selected file at once.",
            "**Opening Circularity again** with files (say, from a shortcut you drop files on) hands them to the copy that's already running. Opening it with no files shows Settings."),
        Heading("Using the wheel in click mode"),
        Table(
            ("Mouse", "Move over a button and click it. Clicking the middle or anywhere outside cancels."),
            ("Arrow keys", "Right or Down goes clockwise around the ring, Left or Up goes the other way. Tab also moves on."),
            ("More", "On More, press Down (or Enter) to go into the arc; Left and Right move along it, and Up or Esc steps back to More. When the arc opens upwards, Up and Down swap."),
            ("Enter or Space", "Chooses the highlighted button."),
            ("Esc", "Closes the wheel without doing anything.")),
        Tip("Turn the shortcut off", "If Ctrl+Alt+C clashes with another app, untick it in Settings → General."));

    private FrameworkElement Formats_() => Page("What it converts",
        Para("The wheel only shows what makes sense for the file you're dropping. Here's everything it can do."),
        Heading("Images"),
        Para("To **PNG, JPG, BMP, GIF, TIFF, WebP, AVIF** and **PDF**, and to **HEIC** on PCs with Microsoft's HEVC extension (many have it already; without it, there's no HEIC button). It reads **iPhone HEIC/HEIF photos, AVIF** and **SVG** on every PC, even without that extension. Photos taken sideways come out the right way up. SVGs keep their transparency; small ones (icons) come out at 1024 px rather than their tiny nominal size, and anything an SVG links to outside itself is ignored, so converting one never goes online."),
        Heading("Video"),
        Para("To **MP4, WebM, MOV, GIF** and **MP3** (just the sound). It reads iPhone HEVC and ProRes videos too."),
        Heading("Audio"),
        Para("To **MP3, WAV, FLAC, OGG** and **M4A**. It reads Apple's CAF, AIFF and Apple Lossless too."),
        Heading("Documents"),
        Bullets(
            "**PDF → PNG or JPG**: one picture per page. Several pages go into a folder called **name (pages)**.",
            "**PDF → TXT**: the text inside.",
            "**Markdown → PDF or HTML**, **HTML → PDF**, **TXT → PDF**, laid out on A4 or US Letter depending on your region."),
        Heading("With Microsoft Office installed"),
        Bullets(
            "**Word** (DOCX, DOC, RTF, ODT) → PDF, DOCX or TXT",
            "**Excel** (XLSX, XLS, ODS, CSV) → PDF",
            "**PowerPoint** (PPTX, PPT, ODP) → PDF"),
        Para("Office runs invisibly. If you already had that Office app open, your copy is used and left open. Without Office, these buttons simply don't appear."),
        Tip("Edits keep the format", "Resizing or cropping an AVIF gives an AVIF, and a HEIC stays HEIC where the PC can save it (otherwise it becomes a JPG). See-through parts stay see-through in AVIF and PNG."));

    private FrameworkElement ResizeAndCrop() => Page("Resize and crop",
        Para("These work on **images and videos**, and open a small window before anything happens."),
        Heading("Resize"),
        Bullets(
            "Type a **width × height**. The window shows the size each file will end up at.",
            "**Keep the aspect ratio** fits the picture inside that size without stretching it. Turn it off to stretch to exactly that size.",
            "The result is saved as **name (resized)**, next to the original."),
        Heading("Crop"),
        Bullets(
            "Opens an editor with a **crop box**. Move it, resize it from any corner or edge, or drag out a new one.",
            "Lock it to **1:1, 4:3, 3:2, 16:9 or 9:16**, or crop freely.",
            "Videos show a frame from about one second in, and the whole video is cropped.",
            "With several files, they're cropped one after another, each with its own box.",
            "The result is saved as **name (cropped)**."),
        Tip("Animated GIFs", "Resizing or cropping an animated GIF uses its first frame only."));

    private FrameworkElement Compress() => Page("Compress",
        Para("Makes files smaller. Compress is offered for **images, videos and PDFs** (but not when you drop a mix of kinds), and the result is saved as **name (compressed)**."),
        Heading("Images"),
        Bullets(
            "By **quality**: a percentage, where lower is smaller.",
            "To a **target size**, such as 500 KB: it keeps the highest quality that fits, and shrinks the picture only if even low quality is too big.",
            "Saved as **JPG**, so transparent areas become white. The notification shows the size before and after."),
        Heading("Videos"),
        Bullets(
            "**Higher quality**, **balanced** or **smaller file**.",
            "Or to a **target size**, such as 25 MB, with buttons for common upload limits.",
            "Optionally at a lower resolution: 1080p, 720p or 480p. The window estimates how big the result will be.",
            "Saved as **H.264 MP4**, which plays everywhere. If a target size would make the picture too blocky, the resolution is lowered automatically."),
        Heading("PDFs"),
        Bullets(
            "**Light** (200 dpi), **balanced** (150 dpi) or **strong** (100 dpi).",
            "Only the pictures inside are made smaller. Text and drawings stay exactly as they were.",
            "Every page is compared with the original afterwards. If any page would look different, the PDF is left alone."),
        Tip("Doing the same thing often?", "Tick **Also add this as a button on the wheel** in the Compress window, and next time it's one drop. See chapter 10."));

    private FrameworkElement TrimAndCombine() => Page("Trim and combine",
        Heading("Trim (videos and audio)"),
        Bullets(
            "Pick the **start** and **end** with the sliders, or type a time such as 1:23.4.",
            "The frame at each cut point is shown, so you can see exactly where it cuts.",
            "**Exact cut** re-encodes, so it starts and ends exactly where you chose.",
            "**Quick cut** copies the video as it is: instant and with no quality loss, but a video may start a little early, at the nearest keyframe before your start.",
            "The result is saved as **name (trimmed)**."),
        Heading("Combine (images and PDFs)"),
        Bullets(
            "Drop **two or more** images and/or PDFs together and choose **Combine**.",
            "You get one PDF with every page, in the order the files were dropped, saved as **name (combined).pdf**."));

    private FrameworkElement OwnButtons() => Page("Your own buttons",
        Para("If you often resize to the same size or compress to the same limit, make it a button."),
        Steps(
            "Use **Resize** or **Compress** as usual.",
            "In its window, tick **Also add this as a button on the wheel** before you start.",
            "Next time, that exact resize or compression is a button on the wheel, named for what it does (say, **1920×1080** or **Under 25 MB**). It runs straight away, without the window."),
        Para("A compression button only appears for the kind of file it was made for: a video one for videos, and so on."),
        Heading("Arrange the wheel"),
        Para("In **Settings → Buttons**, pick a kind of file with the **Preview as** choices, then:"),
        Bullets(
            "Move buttons **up** to give them a place on the ring, or **down** to put them behind More. Each one says where it ends up.",
            "**Untick** buttons you never use to hide them.",
            "**Delete** your own buttons with the delete button next to them.",
            "**Reset order** puts that kind of file back to the built-in order."),
        OpenSettingsButton());

    private FrameworkElement AfterConverting() => Page("After converting",
        Bullets(
            "Conversions run **in the background**, two at a time, so you can keep working.",
            "**Hover the tray icon** to see progress, for example **clip.mov → MP4 43% (+2 more)**.",
            "You get **one notification per drop** when it's done. **Open** opens the new file in its usual app, and **Open folder** (or clicking the notification) shows it in File Explorer.",
            "**Right-click the tray icon → Cancel conversions** stops everything. A cancelled or failed conversion never leaves a half-written file behind."),
        Heading("Where files go"),
        Para("Next to the original, or in a folder you choose in **Settings → Features** (if that folder isn't there, say on an unplugged drive, the file goes next to the original instead). The name says what happened:"),
        Table(
            ("Converted", "photo.png"),
            ("Resized", "photo (resized).jpg"),
            ("Cropped", "photo (cropped).jpg"),
            ("Compressed", "photo (compressed).jpg"),
            ("Trimmed", "clip (trimmed).mp4"),
            ("Combined", "scan (combined).pdf"),
            ("PDF pages", "a folder called report (pages)")),
        Para("If the name is taken, **(1)**, **(2)** and so on are added, so nothing is ever overwritten."),
        Heading("Paste the result anywhere"),
        Para("Turn on **Copy converted files to the clipboard** in Settings → Features, and finished files are copied as if you'd pressed Ctrl+C in File Explorer. Paste them straight into a chat, an email or another folder."));

    private FrameworkElement SettingsAndTray() => Page("Settings and the tray",
        Heading("The tray icon"),
        Para("**Double-click** it to open Settings. **Right-click** it for:"),
        Table(
            ("Settings…", "Everything below."),
            ("Check for updates…", "Shows what's new and installs the latest version in one click."),
            ("Help", "This tutorial, the welcome guide from the first start, and Report a bug."),
            ("Pause gestures", "Stops right-click, Shift and Ctrl+Alt+C from opening the wheel until you untick it, handy for a game or a drawing app."),
            ("Cancel conversions", "Stops everything that's running."),
            ("Open log", "What Circularity has been doing, useful for a bug report."),
            ("Exit", "Closes Circularity until you start it again.")),
        Heading("Settings"),
        Para("Changes apply straight away, and the wheel on the left is a live preview."),
        Table(
            ("Wheel", "Theme (same as Windows, light or dark), size, background opacity and accent colour. The tray icon follows the colour."),
            ("Buttons", "The order of the buttons for each kind of file, and which ones are hidden."),
            ("General", "Which gestures are on (right-click, Shift, Ctrl+Alt+C), notifications, and starting when you sign in to Windows."),
            ("Features", "Where converted files are saved, copying them to the clipboard, and checking for updates automatically."),
            ("Help", "This tutorial, the welcome guide, and Report a bug."),
            ("About", "What Circularity found on this PC, and Copy diagnostics for bug reports.")),
        OpenSettingsButton());

    private FrameworkElement Troubleshooting() => Page("If something isn't working",
        Heading("The wheel doesn't appear"),
        Bullets(
            "Check that **Pause gestures** isn't ticked in the tray menu, and that right-click or Shift is on in **Settings → General**.",
            "Drag from **File Explorer** or the desktop first. Other apps only work if they're Edge, Chrome, Firefox, Brave, Opera, Vivaldi, Outlook or Thunderbird.",
            "It stays out of the way while something is **full screen**.",
            "Make sure Circularity is running: its icon should be in the tray. If not, start it from the Start menu."),
        Heading("The wheel appears but won't take the drop"),
        Para("Windows doesn't allow dropping from an app running **as administrator** onto a normal one. Drag from a normal File Explorer window instead, or use **Ctrl+Alt+C**."),
        Heading("A button is missing"),
        Bullets(
            "Word, Excel and PowerPoint buttons need **Microsoft Office** installed.",
            "Markdown, HTML and TXT → PDF need **WebView2**, which comes with Windows 11 and most up-to-date Windows 10 PCs.",
            "You may have hidden it, or it may be behind **More**: check **Settings → Buttons**."),
        Heading("Known limits"),
        Bullets(
            "HEIC and AVIF can be read but not saved. Without Microsoft's image extensions they take a moment longer to open.",
            "iPhone videos filmed in **HDR** can come out with paler colours.",
            "**PDF → Word** isn't offered, because Word asks a question first that an invisible Word can't answer."),
        Heading("If Circularity runs into an error"),
        Para("It keeps going where it can and shows a notification: **click it** to report what happened. If the error closes it, it **starts again by itself** and offers the report then. Crash reports are kept in **%LOCALAPPDATA%\\Circularity\\crashes**."),
        Heading("Reporting a problem"),
        Para("Right-click the tray icon → **Help → Report a bug** (or use the button in **Settings → Help**). It opens a new bug report on GitHub and copies what Circularity found on this PC, with the end of its log, to the clipboard: press **Ctrl+V** in the report to paste it. It includes folder paths from your PC, so look it over before you send it."),
        Para("To copy the same details without opening a report, use **Copy diagnostics** in **Settings → About**."),
        OpenSettingsButton());

    // --- Building blocks ---

    private static FrameworkElement Page(string title, params UIElement[] content)
    {
        var page = new StackPanel();
        page.Children.Add(new TextBlock { Text = title, FontSize = 26, FontWeight = FontWeights.SemiBold, TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 0, 0, 12) });
        foreach (var element in content)
            page.Children.Add(element);
        return page;
    }

    private static TextBlock Heading(string text) =>
        new() { Text = text, FontSize = 17, FontWeight = FontWeights.SemiBold, TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 18, 0, 6) };

    /// <summary>A paragraph, with **double asterisks** around the parts in bold.</summary>
    private static TextBlock Para(string text)
    {
        var block = new TextBlock { TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 0, 0, 8), LineHeight = 21 };
        AddMarkup(block.Inlines, text);
        return block;
    }

    private static void AddMarkup(InlineCollection inlines, string text)
    {
        var parts = text.Split("**");
        for (int i = 0; i < parts.Length; i++)
        {
            if (parts[i].Length == 0)
                continue;
            inlines.Add(i % 2 == 1 ? new Bold(new Run(parts[i])) : new Run(parts[i]));
        }
    }

    private static UIElement Bullets(params string[] items) => List(items, _ => "•");

    private static UIElement Steps(params string[] items) => List(items, i => $"{i + 1}.");

    private static UIElement List(string[] items, Func<int, string> marker)
    {
        var grid = new Grid { Margin = new Thickness(0, 0, 0, 8) };
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(26) });
        grid.ColumnDefinitions.Add(new ColumnDefinition());
        for (int i = 0; i < items.Length; i++)
        {
            grid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
            var mark = new TextBlock { Text = marker(i), Opacity = 0.7, Margin = new Thickness(4, 0, 0, 6) };
            var text = Para(items[i]);
            text.Margin = new Thickness(0, 0, 0, 6);
            Grid.SetRow(mark, i);
            Grid.SetRow(text, i);
            Grid.SetColumn(text, 1);
            grid.Children.Add(mark);
            grid.Children.Add(text);
        }
        return grid;
    }

    private static UIElement Table(params (string Name, string What)[] rows)
    {
        var grid = new Grid { Margin = new Thickness(0, 2, 0, 10) };
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(170) });
        grid.ColumnDefinitions.Add(new ColumnDefinition());
        for (int i = 0; i < rows.Length; i++)
        {
            grid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
            var name = new TextBlock { Text = rows[i].Name, FontWeight = FontWeights.SemiBold, TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 0, 12, 8) };
            var what = Para(rows[i].What);
            Grid.SetRow(name, i);
            Grid.SetRow(what, i);
            Grid.SetColumn(what, 1);
            grid.Children.Add(name);
            grid.Children.Add(what);
        }
        return grid;
    }

    private UIElement Tip(string title, string text)
    {
        var body = Para(text);
        body.Margin = new Thickness(0, 4, 0, 0);
        return new Border
        {
            Background = _tint,
            BorderBrush = _accent,
            BorderThickness = new Thickness(3, 0, 0, 0),
            CornerRadius = new CornerRadius(6),
            Padding = new Thickness(14, 10, 14, 10),
            Margin = new Thickness(0, 12, 0, 8),
            Child = new StackPanel { Children = { new TextBlock { Text = title, FontWeight = FontWeights.SemiBold, TextWrapping = TextWrapping.Wrap }, body } },
        };
    }

    /// <summary>Text on the left and a picture (a wheel) on the right.</summary>
    private static UIElement Beside(FrameworkElement picture, params UIElement[] text)
    {
        var grid = new Grid();
        grid.ColumnDefinitions.Add(new ColumnDefinition());
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        var column = new StackPanel { Margin = new Thickness(0, 0, 24, 0) };
        foreach (var element in text)
            column.Children.Add(element);
        Grid.SetColumn(picture, 1);
        grid.Children.Add(column);
        grid.Children.Add(picture);
        return grid;
    }

    /// <summary>The same neutral grey backdrop as Settings and the welcome, which never clashes with the accent.</summary>
    private static Border Backdrop(double size, UIElement content) => new()
    {
        Width = size,
        Height = size,
        CornerRadius = new CornerRadius(12),
        ClipToBounds = true,
        VerticalAlignment = VerticalAlignment.Top,
        Background = new LinearGradientBrush(new GradientStopCollection
        {
            new(Color.FromRgb(0xA3, 0xA8, 0xB2), 0),
            new(Color.FromRgb(0x7C, 0x82, 0x8D), 0.5),
            new(Color.FromRgb(0x55, 0x5B, 0x66), 1),
        }, new Point(0, 0), new Point(1, 1)),
        Child = new Viewbox { Stretch = Stretch.Uniform, StretchDirection = StretchDirection.DownOnly, Margin = new Thickness(6), Child = content },
    };

    /// <summary>A small wheel that lights up each button in turn, like the welcome's.</summary>
    private FrameworkElement CyclingWheel(string sample, string size)
    {
        var small = _settings.Clone();
        small.WheelSize = 240;
        var wheel = new WheelView();
        wheel.ApplySettings(small);
        var (_, actions) = Formats.ActionsFor(new[] { sample }, _canHandle, _settings);
        wheel.SetActions(actions, sample, size);

        var ring = wheel.RingOrder;
        int next = 0;
        var timer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(850) };
        timer.Tick += (_, _) =>
        {
            if (ring.Count == 0)
                return;
            wheel.SetHot(ring[next]);
            next = (next + 1) % ring.Count;
        };
        timer.Start();
        _timers.Add(timer);
        wheel.Loaded += (_, _) => wheel.PlayIntro();
        return Backdrop(300, wheel);
    }

    /// <summary>A wheel to hover and click, for each kind of file, saying what letting go would do.</summary>
    private FrameworkElement LiveWheel()
    {
        var wheel = new WheelView();
        wheel.ApplySettings(_settings);
        var status = new TextBlock { TextWrapping = TextWrapping.Wrap, FontSize = 15, Margin = new Thickness(0, 4, 0, 0) };

        // Null while the mouse is elsewhere in the window.
        void Describe(int? index)
        {
            status.Inlines.Clear();
            if (index == null)
            {
                AddMarkup(status.Inlines, "Move the mouse over the wheel to see what letting go on each part would do.");
                return;
            }
            int at = index.Value;
            string what = at switch
            {
                WheelView.CenterIndex => "**Cancel**: nothing happens.",
                WheelView.MoreIndex => "**More**: the arc opens. Move onto one of its buttons.",
                >= 0 when at < wheel.Actions.Count => $"**{WhatItDoes(wheel.Actions[at])}**",
                _ => "Off the wheel: it closes, and the drag carries on as normal.",
            };
            AddMarkup(status.Inlines, "Letting go here: " + what);
        }

        void Show(string sample, string size)
        {
            var (label, actions) = Formats.ActionsFor(new[] { sample }, _canHandle, _settings);
            if (actions.Length > 0)
                wheel.SetActions(actions, sample, size);
            else
                wheel.SetActions(actions, label);
            wheel.SetHot(-1);
            Describe(null);
        }

        wheel.MouseMove += (_, e) =>
        {
            int index = wheel.HitTest(e.GetPosition(wheel));
            wheel.SetHot(index);
            Describe(index);
        };
        wheel.MouseLeave += (_, _) =>
        {
            wheel.SetHot(-1);
            Describe(null);
        };
        wheel.MouseLeftButtonUp += (_, e) =>
        {
            int index = wheel.HitTest(e.GetPosition(wheel));
            if (index == WheelView.CenterIndex)
                wheel.PlayIntro();
            else
                wheel.Flash(index);
        };

        var kinds = new WrapPanel { Margin = new Thickness(0, 12, 0, 0) };
        kinds.Children.Add(new TextBlock { Text = "Try it with", VerticalAlignment = VerticalAlignment.Center, Opacity = 0.75, Margin = new Thickness(0, 0, 14, 0) });
        string group = "TutorialKind" + Guid.NewGuid().ToString("N");
        foreach (var (name, sample, size) in new[] { ("Image", "photo.jpg", "3.1 MB"), ("Video", "clip.mp4", "48 MB"), ("Audio", "song.mp3", "7.2 MB"), ("Document", "report.pdf", "220 KB") })
        {
            var option = new RadioButton { Content = name, GroupName = group, MinWidth = 0, Margin = new Thickness(0, 0, 14, 0) };
            option.Checked += (_, _) => Show(sample, size);
            kinds.Children.Add(option);
        }

        // A fixed width, and room for two lines of description, so the text changing as the mouse moves
        // never resizes the panel and shifts the wheel (or the page below it) around.
        var panel = new StackPanel { Width = 400, Margin = new Thickness(0, 4, 0, 4) };
        status.MinHeight = 2 * 22;
        panel.Children.Add(Backdrop(400, wheel));
        panel.Children.Add(kinds);
        panel.Children.Add(status);
        ((RadioButton)kinds.Children[1]).IsChecked = true;
        wheel.Loaded += (_, _) => wheel.PlayIntro();
        return new Border { HorizontalAlignment = HorizontalAlignment.Left, Child = panel };
    }

    private static string WhatItDoes(WheelAction action) => action.Kind switch
    {
        WheelActionKind.Convert => $"Convert to {action.Label}",
        WheelActionKind.Preset => $"{action.Label} (your own button), straight away",
        WheelActionKind.Combine => "Combine into one PDF",
        _ => $"{action.Label}, after a small window to choose how",
    };

    private UIElement OpenSettingsButton()
    {
        var button = new Button { Content = "Open settings", HorizontalAlignment = HorizontalAlignment.Left, Margin = new Thickness(0, 12, 0, 0) };
        button.Click += (_, _) => OpenSettingsRequested?.Invoke();
        return button;
    }

    private UIElement PracticeButton()
    {
        var result = new TextBlock { TextWrapping = TextWrapping.Wrap, Opacity = 0.75, VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(12, 0, 0, 0) };
        var button = new Button { Content = "Make a practice picture" };
        button.Click += (_, _) =>
        {
            try
            {
                string path = WritePracticePicture();
                Process.Start("explorer.exe", $"/select,\"{path}\"");
                result.Text = "File Explorer is open with it selected. Drag it, and tap right-click while dragging.";
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or System.ComponentModel.Win32Exception)
            {
                Log.Write($"Couldn't make the practice picture: {ex.Message}");
                result.Text = "Couldn't make it. Try any picture of your own instead.";
            }
        };
        var row = new DockPanel { Margin = new Thickness(0, 4, 0, 8) };
        row.Children.Add(button);
        row.Children.Add(result);
        return row;
    }

    /// <summary>Draws a colourful picture into "Circularity practice" in the temp folder and returns its path.</summary>
    private string WritePracticePicture()
    {
        const int width = 1200, height = 800;
        var visual = new DrawingVisual();
        using (var dc = visual.RenderOpen())
        {
            var sky = new LinearGradientBrush(Color.FromRgb(0x5B, 0x7F, 0xD6), Color.FromRgb(0x2A, 0x9D, 0x8F), new Point(0, 0), new Point(1, 1));
            dc.DrawRectangle(sky, null, new Rect(0, 0, width, height));
            dc.DrawEllipse(new SolidColorBrush(_settings.Accent), null, new Point(width * 0.7, height * 0.38), 150, 150);
            dc.DrawEllipse(new SolidColorBrush(Color.FromArgb(0x55, 0xFF, 0xFF, 0xFF)), null, new Point(width * 0.28, height * 0.62), 230, 230);
            var text = new FormattedText("Practice picture", System.Globalization.CultureInfo.CurrentUICulture, FlowDirection.LeftToRight,
                new Typeface("Segoe UI Semibold"), 64, Brushes.White, 1.0);
            dc.DrawText(text, new Point(60, height - 130));
        }
        var bitmap = new RenderTargetBitmap(width, height, 96, 96, PixelFormats.Pbgra32);
        bitmap.Render(visual);

        string folder = Path.Combine(Path.GetTempPath(), "Circularity practice");
        Directory.CreateDirectory(folder);
        string path = Path.Combine(folder, "practice picture.png");
        var encoder = new PngBitmapEncoder();
        encoder.Frames.Add(BitmapFrame.Create(bitmap));
        using (var file = File.Create(path))
            encoder.Save(file);
        return path;
    }
}
