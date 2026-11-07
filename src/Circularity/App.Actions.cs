using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Windows;
using Circularity.Engine;
using Forms = System.Windows.Forms;

namespace Circularity;

// A wheel button chosen, and watched folders' new files.
public partial class App
{
    private async void OnActionChosen(string[] files, WheelAction action)
    {
        CountUse(files, action);
        if (action.Kind == WheelActionKind.Convert)
        {
            _queue!.Enqueue(files, new ConvertTo(action.Target!));
            return;
        }
        if (action.Kind == WheelActionKind.Combine)
        {
            // One job for the whole drop, in the order Explorer gave the files.
            _queue!.Enqueue(new[] { (files[0], (Operation)new CombineIntoPdf(files)) });
            return;
        }
        if (action.Preset is { } preset)
        {
            _queue!.Enqueue(files, preset.Operation);
            return;
        }

        if (_dialogOpen)
        {
            Notify("Finish the open window first", "A resize, crop, compress or trim window is already open.", Forms.ToolTipIcon.Info);
            return;
        }

        _dialogOpen = true;
        try
        {
            switch (action.Kind)
            {
                case WheelActionKind.Resize:
                    await ResizeAsync(files);
                    break;
                case WheelActionKind.Crop:
                    await CropAsync(files);
                    break;
                case WheelActionKind.Compress when Formats.KindOf(files[0]) == FileKind.Video:
                    await CompressVideoAsync(files);
                    break;
                case WheelActionKind.Compress when Formats.KindOf(files[0]) == FileKind.Document:
                    CompressPdf(files);
                    break;
                case WheelActionKind.Compress:
                    Compress(files);
                    break;
                case WheelActionKind.Trim:
                    await TrimAsync(files);
                    break;
            }
        }
        catch (Exception ex)
        {
            Log.Write($"{action.Label} failed: {ex}");
            Notify($"{action.Label} failed", ex.Message, Forms.ToolTipIcon.Warning);
        }
        finally
        {
            _dialogOpen = false;
            // The crop window holds the whole picture; let it go even if nothing was converted.
            ScheduleMemoryRelease();
        }
    }

    /// <summary>
    /// For "Adapt to how you use it": one more use of this button for this kind of file. Only while the
    /// setting is on, and only for a single kind (a mix has no order of its own).
    /// </summary>
    private void CountUse(string[] files, WheelAction action)
    {
        if (!_settings.AdaptToUse || action.Kind == WheelActionKind.More)
            return;
        var kinds = files.Select(Formats.KindOf).Distinct().ToList();
        if (kinds.Count != 1)
            return;
        // The wheel shares this settings object and works its buttons out afresh each time it opens.
        _settings.CountUse(kinds[0], action.Id);
        _settings.Save();
    }

    /// <summary>New files in a watched folder: each one its action suits is converted, as if dropped on its button.</summary>
    private void OnWatchedFilesArrived(WatchedFolder folder, IReadOnlyList<string> files)
    {
        // The folder may have been changed or removed in Settings since these were noticed.
        if (!_settings.WatchedFolders.Contains(folder) || !folder.Enabled)
            return;
        var operation = folder.OperationFor(_settings);
        if (operation == null)
        {
            Log.Write($"Watched folder {folder.Path}: its preset was deleted, so new files are left alone.");
            return;
        }
        var preset = _settings.Presets.FirstOrDefault(p => WatchedFolder.PresetPrefix + p.Id == folder.Action);

        var suited = files.Where(f => File.Exists(f) && Formats.Suits(f, operation, preset) && _queue!.CanHandle(f, operation)).ToList();
        foreach (string skipped in files.Except(suited))
            Log.Write($"Watched folder {folder.Path}: {operation.Describe()} doesn't apply to {Path.GetFileName(skipped)}, so it's left alone.");
        if (suited.Count > 0)
        {
            Log.Write($"Watched folder {folder.Path}: {operation.Describe()} for {suited.Count} new file(s).");
            _queue!.Enqueue(suited, operation);
        }
    }
}
