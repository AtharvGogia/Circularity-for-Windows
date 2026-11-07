using System;
using System.Collections.Generic;
using System.IO;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Media.Imaging;
using Circularity.Engine;
using Forms = System.Windows.Forms;

namespace Circularity;

// The tool windows (resize, compress, trim, crop) that run before converting.
public partial class App
{
    private async Task ResizeAsync(string[] files)
    {
        (int, int)? size = null;
        if (files.Length == 1)
        {
            try
            {
                size = Formats.KindOf(files[0]) == FileKind.Video
                    ? await _ffmpeg!.ProbeVideoSizeAsync(files[0])
                    : ImageConverter.ReadSize(files[0]);
            }
            catch (Exception ex)
            {
                // The dialog works without the current size; it just can't show a preview of the result.
                Log.Write($"Couldn't read the size of {files[0]}: {ex.Message}");
            }
        }

        var dialog = Dialog(new ResizeDialog(files, size));
        if (dialog.ShowDialog() == true && dialog.Result != null)
        {
            _queue!.Enqueue(files, new ResizeTo(dialog.Result));
            if (dialog.PresetName is { } name)
                AddPreset(new Preset(Preset.NewId(), name, Resize: dialog.Result));
        }
    }

    private void Compress(string[] files)
    {
        var dialog = Dialog(new CompressDialog(files));
        if (dialog.ShowDialog() == true && dialog.Result != null)
        {
            _queue!.Enqueue(files, new CompressTo(dialog.Result));
            if (dialog.PresetName is { } name)
                AddPreset(new Preset(Preset.NewId(), name, Compress: dialog.Result, Kind: nameof(FileKind.Image)));
        }
    }

    private async Task CompressVideoAsync(string[] files)
    {
        MediaInfo? info = null;
        if (files.Length == 1)
        {
            try
            {
                info = await _ffmpeg!.ProbeAsync(files[0]);
            }
            catch (Exception ex)
            {
                // The dialog works without it; it just can't estimate the result's size.
                Log.Write($"Couldn't read {files[0]}: {ex.Message}");
            }
        }
        var dialog = Dialog(new VideoCompressDialog(files, info));
        if (dialog.ShowDialog() == true && dialog.Result != null)
        {
            _queue!.Enqueue(files, new CompressTo(dialog.Result));
            if (dialog.PresetName is { } name)
                AddPreset(new Preset(Preset.NewId(), name, Compress: dialog.Result, Kind: nameof(FileKind.Video)));
        }
    }

    private void CompressPdf(string[] files)
    {
        var dialog = Dialog(new PdfCompressDialog(files));
        if (dialog.ShowDialog() == true && dialog.Result != null)
        {
            _queue!.Enqueue(files, new CompressTo(dialog.Result));
            if (dialog.PresetName is { } name)
                AddPreset(new Preset(Preset.NewId(), name, Compress: dialog.Result, Kind: nameof(FileKind.Document)));
        }
    }

    /// <summary>One trim window per file, like cropping; cancelling one stops the rest.</summary>
    private async Task TrimAsync(string[] files)
    {
        var items = new List<(string File, Operation Operation)>();
        for (int i = 0; i < files.Length; i++)
        {
            string file = files[i];
            MediaInfo info;
            try
            {
                info = await _ffmpeg!.ProbeAsync(file);
            }
            catch (Exception ex)
            {
                Log.Write($"Couldn't read {file} for trimming: {ex}");
                Notify("Couldn't open file", $"{Path.GetFileName(file)}: {ex.Message}", Forms.ToolTipIcon.Warning);
                continue;
            }
            if (info.Duration <= TimeSpan.FromSeconds(0.2))
            {
                Notify("Nothing to trim", $"{Path.GetFileName(file)} is too short to trim.", Forms.ToolTipIcon.Info);
                continue;
            }

            Func<TimeSpan, Task<System.Windows.Media.ImageSource?>>? frameAt = info.HasVideo ? at => FrameAtAsync(file, at) : null;
            var window = Dialog(new TrimDialog(file, info.Duration, files.Length > 1 ? $"{i + 1} of {files.Length}" : null, frameAt));
            if (window.ShowDialog() != true || window.Result == null)
                return;
            items.Add((file, window.Result));
        }
        if (items.Count > 0)
            _queue!.Enqueue(items);
    }

    /// <summary>A small picture of the frame at a time, for the trim window; null if there isn't one.</summary>
    private async Task<System.Windows.Media.ImageSource?> FrameAtAsync(string file, TimeSpan at)
    {
        string png = Path.Combine(Path.GetTempPath(), $"Circularity-frame-{Guid.NewGuid():N}.png");
        try
        {
            if (!await _ffmpeg!.ExtractFrameAtAsync(file, at, png, 480))
                return null;
            return await Task.Run(() => ImageConverter.LoadOriented(png));
        }
        catch (Exception ex)
        {
            Log.Write($"Couldn't show the frame at {at} of {file}: {ex.Message}");
            return null;
        }
        finally
        {
            File.Delete(png);
        }
    }

    /// <summary>Saves a new wheel button; it goes at the end of the order, so usually behind More until moved.</summary>
    private void AddPreset(Preset preset)
    {
        var settings = _settings.Clone();
        settings.Presets.Add(preset);
        settings.Save();
        ApplySettings(settings);
        _settingsWindow?.Reload(settings);
        Log.Write($"Added a wheel button '{preset.Name}' ({preset.Operation.Describe()}).");
    }

    private async Task CropAsync(string[] files)
    {
        var items = new List<(string File, Operation Operation)>();
        for (int i = 0; i < files.Length; i++)
        {
            string file = files[i];
            BitmapSource picture;
            try
            {
                picture = await LoadCropPreviewAsync(file);
            }
            catch (Exception ex)
            {
                Log.Write($"Couldn't open {file} for cropping: {ex}");
                Notify("Couldn't open file", $"{Path.GetFileName(file)}: {ex.Message}", Forms.ToolTipIcon.Warning);
                continue;
            }

            var window = Dialog(new CropWindow(picture, Path.GetFileName(file), i + 1, files.Length));
            window.ShowDialog();
            if (window.Outcome == CropOutcome.Cancelled)
                return;
            if (window.Outcome == CropOutcome.Cropped)
                items.Add((file, new CropTo(window.Result)));
        }

        if (items.Count > 0)
            _queue!.Enqueue(items);
    }

    /// <summary>The picture itself, or for a video a frame from about a second in.</summary>
    private async Task<BitmapSource> LoadCropPreviewAsync(string file)
    {
        if (Formats.KindOf(file) != FileKind.Video)
            return await Task.Run(() => ImageConverter.LoadOriented(file));

        string frame = Path.Combine(Path.GetTempPath(), $"Circularity-frame-{Guid.NewGuid():N}.png");
        try
        {
            await _ffmpeg!.ExtractFrameAsync(file, frame);
            return await Task.Run(() => ImageConverter.LoadOriented(frame));
        }
        finally
        {
            File.Delete(frame);
        }
    }
}
