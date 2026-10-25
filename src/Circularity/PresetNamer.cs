using System;
using System.Windows;
using System.Windows.Controls;

namespace Circularity;

/// <summary>
/// The "also add this as a button on the wheel" option in the Resize and Compress windows: a checkbox
/// that reveals a name box. The name follows the chosen settings ("1920×1080", "80% quality") until
/// the user types their own.
/// </summary>
internal sealed class PresetNamer
{
    private readonly CheckBox _check;
    private readonly FrameworkElement _panel;
    private readonly TextBox _box;
    private readonly Action _changed;
    private bool _typed;
    private bool _suggesting;

    public PresetNamer(CheckBox check, FrameworkElement panel, TextBox box, Action changed)
    {
        _check = check;
        _panel = panel;
        _box = box;
        _changed = changed;
        _box.MaxLength = Preset.MaxNameLength;

        _check.Click += (_, _) =>
        {
            _panel.Visibility = _check.IsChecked == true ? Visibility.Visible : Visibility.Collapsed;
            if (_check.IsChecked == true)
            {
                _box.Focus();
                _box.SelectAll();
            }
            _changed();
        };
        _box.TextChanged += (_, _) =>
        {
            if (!_suggesting)
                _typed = true;
            _changed();
        };
    }

    /// <summary>The name to save under, or null when no button was asked for.</summary>
    public string? Name => _check.IsChecked == true ? _box.Text.Trim() : null;

    /// <summary>False only while a button is asked for without a name.</summary>
    public bool IsValid => Name is not { Length: 0 };

    /// <summary>Offers a name for the current settings, unless the user has typed one.</summary>
    public void Suggest(string name)
    {
        if (_typed || _box.Text == name)
            return;
        _suggesting = true;
        _box.Text = name.Length > Preset.MaxNameLength ? name[..Preset.MaxNameLength] : name;
        _suggesting = false;
    }
}
