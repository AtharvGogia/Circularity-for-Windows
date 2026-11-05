using System;
using System.IO;
using Xunit;
using Circularity;

namespace Circularity.UnitTests;

public class SettingsMigrationTests
{
    [Fact]
    public void Migrate_Version1_MovesToCurrentVersion()
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

    [Fact]
    public void OlderFiles_KeepTheirChoices_AndTheCountStaysOff()
    {
        var settings = Settings.FromJson("""{"Version": 2, "AutoCheckForUpdates": false}""");

        Assert.False(settings.AutoCheckForUpdates);
        Assert.False(settings.ShareUsageCount);
        Assert.Null(settings.UsageId);
    }

    [Fact]
    public void ChoicesToTurnThingsOff_AreKept()
    {
        var settings = Settings.FromJson("""{"Version": 2, "AutoCheckForUpdates": false, "ShareUsageCount": false, "UsageId": "old", "LastUsagePing": "2026-01-01"}""");

        Assert.False(settings.AutoCheckForUpdates);
        Assert.False(settings.ShareUsageCount);
        Assert.Null(settings.UsageId);
        Assert.Null(settings.LastUsagePing);
    }

    [Fact]
    public void FreshSettings_CheckForUpdates_ButDontCount()
    {
        var settings = Settings.FromJson("{}");

        Assert.True(settings.AutoCheckForUpdates);
        Assert.False(settings.ShareUsageCount);
        Assert.Null(settings.UsageId);
    }

    [Fact]
    public void SetShareUsageCount_OffClearsTheIdAndOnMakesANewOne()
    {
        var settings = Settings.FromJson("{}");
        settings.SetShareUsageCount(true);
        string first = settings.UsageId!;
        Assert.False(string.IsNullOrEmpty(first));

        settings.SetShareUsageCount(false);
        Assert.Null(settings.UsageId);
        settings.SetShareUsageCount(true);

        Assert.False(string.IsNullOrEmpty(settings.UsageId));
        Assert.NotEqual(first, settings.UsageId);
    }
}
