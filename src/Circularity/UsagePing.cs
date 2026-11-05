using System;
using System.Globalization;
using System.Net.Http;
using System.Text;
using System.Text.Json;
using System.Threading.Tasks;

namespace Circularity;

/// <summary>
/// The anonymous daily count of how many people use Circularity. Once a day it sends two things, a random
/// ID (made on this PC, not linked to anything) and the version number, as {"id":"...","version":"x.y.z"},
/// and nothing else: no files, names, addresses or settings. It can be turned off in Settings, which also
/// throws the ID away.
/// </summary>
internal sealed class UsagePing
{
    public const string DefaultUrl = "https://circularity-usage.gogiaatharv.workers.dev/ping";

    private const string DateFormat = "yyyy-MM-dd";

    private readonly HttpClient _http;
    private readonly string _url;

    /// <param name="http">Injectable for tests; defaults to a client that identifies itself as Circularity.</param>
    public UsagePing(HttpClient? http = null, string? url = null)
    {
        _http = http ?? CreateClient();
        _url = url ?? DefaultUrl;
    }

    /// <summary>The date as saved in <see cref="Settings.LastUsagePing"/>.</summary>
    public static string FormatDate(DateOnly date) => date.ToString(DateFormat, CultureInfo.InvariantCulture);

    /// <summary>True unless the count was already sent today. A missing or unreadable date, or one in the future (a clock set back), counts as due.</summary>
    public static bool IsDue(string? lastPing, DateOnly todayUtc) =>
        !DateOnly.TryParseExact(lastPing, DateFormat, CultureInfo.InvariantCulture, DateTimeStyles.None, out var last) || last != todayUtc;

    /// <summary>Sends the count; true if it was accepted. Never throws: a failure is logged and tried again later.</summary>
    public async Task<bool> SendAsync(string id)
    {
        try
        {
            var v = UpdateChecker.CurrentVersion;
            string body = JsonSerializer.Serialize(new { id, version = $"{v.Major}.{v.Minor}.{v.Build}" });
            using var content = new StringContent(body, Encoding.UTF8, "application/json");
            using var response = await _http.PostAsync(_url, content);
            if (response.IsSuccessStatusCode)
                return true;
            Log.Write($"Usage count not accepted: {(int)response.StatusCode}");
        }
        catch (Exception ex)
        {
            Log.Write($"Couldn't send the usage count: {ex.Message}");
        }
        return false;
    }

    private static HttpClient CreateClient()
    {
        var client = new HttpClient { Timeout = TimeSpan.FromSeconds(30) };
        client.DefaultRequestHeaders.UserAgent.ParseAdd($"Circularity/{UpdateChecker.CurrentVersion}");
        return client;
    }
}
