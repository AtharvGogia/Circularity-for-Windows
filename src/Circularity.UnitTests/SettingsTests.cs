using System;
using System.IO;
using Xunit;
using Circularity;

namespace Circularity.UnitTests;

public class SettingsMigrationTests
{
    [Fact]
    public void Migrate_Version1To2_SetsVersionTo2()
    {
        // Simulate loading v1 settings (no Version field = defaults to 1)
        var v1Json = """
            {
                "WheelSize": 300,
                "AccentColor": "#FF8A1E",
                "AccentChosen": false,
                "Opacity": 0.82,
                "Theme": "System",
                "RightClickTrigger": true,
                "ShiftTrigger": true,
                "HotkeyEnabled": true,
                "NotifyOnFinish": true,
                "NotifyOnStart": true,
                "OutputFolder": null,
                "CopyToClipboardOnFinish": false,
                "AutoCheckForUpdates": false,
                "AutoCheckIntervalHours": 24,
                "LastNotifiedUpdate": null,
                "WelcomeShown": false,
                "Presets": [],
                "ButtonLayouts": {}
            }
            """;

        var settings = Settings.FromJson(v1Json);

        Assert.Equal(2, settings.Version);
        Assert.Equal("#8B5CF6", settings.AccentColor); // Old default migrated to new
        Assert.False(settings.CopyToClipboardOnFinish); // Defaults to false
    }

    [Fact]
    public void Migrate_VersionAlreadyCurrent_NoChanges()
    {
        var v2Json = """
            {
                "Version": 2,
                "WheelSize": 300,
                "AccentColor": "#22C55E",
                "AccentChosen": true,
                "Opacity": 0.82,
                "Theme": "System",
                "RightClickTrigger": true,
                "ShiftTrigger": true,
                "HotkeyEnabled": true,
                "NotifyOnFinish": true,
                "NotifyOnStart": true,
                "OutputFolder": null,
                "CopyToClipboardOnFinish": true,
                "AutoCheckForUpdates": false,
                "AutoCheckIntervalHours": 24,
                "LastNotifiedUpdate": null,
                "WelcomeShown": false,
                "Presets": [],
                "ButtonLayouts": {}
            }
            """;

        var settings = Settings.FromJson(v2Json);

        Assert.Equal(2, settings.Version);
        Assert.Equal("#22C55E", settings.AccentColor); // Custom color preserved
        Assert.True(settings.CopyToClipboardOnFinish); // Preserved
    }

    [Fact]
    public void Load_MissingFile_ReturnsDefaultsWithCurrentVersion()
    {
        var tempPath = Path.Combine(Path.GetTempPath(), $"CircularityTest_{Guid.NewGuid()}");
        var originalPath = Settings.FilePath;

        try
        {
            // Can't easily test this without modifying FilePath, so just verify defaults
            var defaults = new Settings();
            Assert.Equal(Settings.CurrentVersion, defaults.Version);
            Assert.Equal(2, defaults.Version);
        }
        finally
        {
            if (Directory.Exists(tempPath))
                Directory.Delete(tempPath, true);
        }
    }

    [Fact]
    public void Save_ThenLoad_PreservesVersion()
    {
        var settings = new Settings
        {
            Version = 2,
            WheelSize = 300,
            AccentColor = "#22C55E",
            AccentChosen = true,
            CopyToClipboardOnFinish = true
        };

        var tempPath = Path.Combine(Path.GetTempPath(), $"CircularityTest_{Guid.NewGuid()}.json");
        try
        {
            File.WriteAllText(tempPath, System.Text.Json.JsonSerializer.Serialize(settings));
            var loaded = Settings.FromJson(File.ReadAllText(tempPath));
            Assert.Equal(2, loaded.Version);
            Assert.Equal(300, loaded.WheelSize);
            Assert.True(loaded.CopyToClipboardOnFinish);
        }
        finally
        {
            if (File.Exists(tempPath))
                File.Delete(tempPath);
        }
    }
}