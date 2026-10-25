using System;
using System.Globalization;

namespace Circularity;

/// <summary>Video and audio times as people write them: "2:31", "1:02:03", "0:07.5".</summary>
internal static class Times
{
    /// <summary>To the second: "2:31", or "1:02:03" for an hour or more.</summary>
    public static string Format(TimeSpan time) =>
        time.TotalHours >= 1 ? time.ToString(@"h\:mm\:ss", CultureInfo.InvariantCulture) : time.ToString(@"m\:ss", CultureInfo.InvariantCulture);

    /// <summary>To a tenth of a second, for picking cut points: "2:31.4".</summary>
    public static string FormatPrecise(TimeSpan time)
    {
        // Round to a tenth first, so 2:59.96 shows as 3:00.0 rather than 2:59.10.
        time = TimeSpan.FromSeconds(Math.Round(time.TotalSeconds, 1));
        string tenths = (time.Milliseconds / 100).ToString(CultureInfo.InvariantCulture);
        return $"{Format(time)}.{tenths}";
    }

    /// <summary>Reads "1:23.4", "1:02:03" or plain seconds ("83.4"); either "." or "," for the decimal point.</summary>
    public static bool TryParse(string text, out TimeSpan time)
    {
        time = TimeSpan.Zero;
        var parts = text.Trim().Replace(',', '.').Split(':');
        if (parts.Length is 0 or > 3)
            return false;
        double seconds = 0;
        foreach (string part in parts)
        {
            if (!double.TryParse(part, NumberStyles.Float, CultureInfo.InvariantCulture, out double value) || value < 0)
                return false;
            seconds = seconds * 60 + value;
        }
        time = TimeSpan.FromSeconds(seconds);
        return true;
    }
}
