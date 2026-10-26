using System;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Text.Json;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;

namespace Circularity;

/// <summary>The newest published release.</summary>
/// <param name="InstallerUrl">The CircularitySetup-x.y.z.exe attached to the release, if any.</param>
internal sealed record UpdateInfo(Version Version, string Tag, string PageUrl, string? InstallerUrl, string Notes)
{
    public bool IsNewer => Version > UpdateChecker.CurrentVersion;
}

/// <summary>
/// Checks GitHub Releases for a newer Circularity and downloads its installer. Only runs when the user
/// asks. Releases are expected to be tagged like "v0.3.0" with CircularitySetup-0.3.0.exe attached.
/// </summary>
internal sealed partial class UpdateChecker
{
    public const string Repository = "AtharvGogia/Circularity-for-Windows";
    public const string ReleasesPage = $"https://github.com/{Repository}/releases";

    private readonly HttpClient _http;
    private readonly string _latestReleaseUrl;

    /// <param name="http">Injectable for tests; defaults to a client that identifies itself to GitHub.</param>
    public UpdateChecker(HttpClient? http = null, string? latestReleaseUrl = null)
    {
        _http = http ?? CreateClient();
        _latestReleaseUrl = latestReleaseUrl ?? $"https://api.github.com/repos/{Repository}/releases/latest";
    }

    /// <summary>This build's version: major.minor.patch, plus a fourth number for a small follow-up release (0.3.3.1).</summary>
    public static Version CurrentVersion
    {
        get
        {
            var v = typeof(UpdateChecker).Assembly.GetName().Version ?? new Version(0, 0, 0);
            return new Version(v.Major, v.Minor, Math.Max(0, v.Build), Math.Max(0, v.Revision));
        }
    }

    /// <summary>The latest release, or null if none has been published (or the repository isn't public).</summary>
    public async Task<UpdateInfo?> GetLatestAsync(CancellationToken cancellationToken = default)
    {
        using var response = await _http.GetAsync(_latestReleaseUrl, cancellationToken);
        if (response.StatusCode == HttpStatusCode.NotFound)
            return null;
        response.EnsureSuccessStatusCode();

        using var json = JsonDocument.Parse(await response.Content.ReadAsStringAsync(cancellationToken));
        var release = json.RootElement;
        string tag = release.GetProperty("tag_name").GetString() ?? "";
        if (!TryParseVersion(tag, out var version))
            throw new InvalidOperationException($"The latest release's tag \"{tag}\" isn't a version like v1.2.3.");

        string? installer = null;
        if (release.TryGetProperty("assets", out var assets))
        {
            installer = assets.EnumerateArray()
                .Where(a => InstallerName().IsMatch(a.GetProperty("name").GetString() ?? ""))
                .Select(a => a.GetProperty("browser_download_url").GetString())
                .FirstOrDefault();
        }

        string page = release.TryGetProperty("html_url", out var url) ? url.GetString() ?? ReleasesPage : ReleasesPage;
        string notes = release.TryGetProperty("body", out var body) ? body.GetString() ?? "" : "";
        return new UpdateInfo(version, tag, page, installer, notes.Trim());
    }

    /// <summary>
    /// Downloads the release's installer to a temporary folder and checks it really is a Circularity
    /// installer before returning its path.
    /// </summary>
    public async Task<string> DownloadInstallerAsync(UpdateInfo update, IProgress<double>? progress, CancellationToken cancellationToken = default)
    {
        if (update.InstallerUrl is not { } url)
            throw new InvalidOperationException("This release has no installer attached.");
        var uri = new Uri(url);
        // Only ever download from GitHub itself (it redirects to its own download servers).
        if (uri.Scheme != Uri.UriSchemeHttps || !uri.Host.Equals("github.com", StringComparison.OrdinalIgnoreCase))
            throw new InvalidOperationException($"Refusing to download an update from {uri.Host}.");

        string folder = Path.Combine(Path.GetTempPath(), "Circularity-update");
        Directory.CreateDirectory(folder);
        string path = Path.Combine(folder, Path.GetFileName(uri.LocalPath));

        using (var response = await _http.GetAsync(uri, HttpCompletionOption.ResponseHeadersRead, cancellationToken))
        {
            response.EnsureSuccessStatusCode();
            long? total = response.Content.Headers.ContentLength;
            await using var source = await response.Content.ReadAsStreamAsync(cancellationToken);
            await using var target = File.Create(path);
            var buffer = new byte[81920];
            long received = 0;
            int read;
            while ((read = await source.ReadAsync(buffer, cancellationToken)) > 0)
            {
                await target.WriteAsync(buffer.AsMemory(0, read), cancellationToken);
                received += read;
                if (total > 0)
                    progress?.Report((double)received / total.Value);
            }
        }

        if (!LooksLikeOurInstaller(path))
        {
            File.Delete(path);
            throw new InvalidOperationException("The downloaded file isn't a Circularity installer, so it was deleted.");
        }
        return path;
    }

    /// <summary>A Windows program whose version information says it's Circularity's setup, by Atharv Gogia.</summary>
    public static bool LooksLikeOurInstaller(string path)
    {
        using (var file = File.OpenRead(path))
        {
            if (file.ReadByte() != 'M' || file.ReadByte() != 'Z')
                return false;
        }
        // Inno Setup pads these fields with spaces.
        var info = FileVersionInfo.GetVersionInfo(path);
        return info.CompanyName?.Trim() == "Atharv Gogia" && info.FileDescription?.Trim() == "Circularity Setup";
    }

    /// <summary>
    /// Runs the installer as a quiet upgrade (a progress window, no questions); it closes this copy of
    /// Circularity and starts the new one when done.
    /// </summary>
    public static void StartInstaller(string installerPath) =>
        Process.Start(new ProcessStartInfo(installerPath, "/SILENT /SUPPRESSMSGBOXES /NORESTART /RELAUNCH=1") { UseShellExecute = true });

    public static bool TryParseVersion(string tag, out Version version)
    {
        var match = VersionTag().Match(tag);
        // Always four numbers, so "v0.3.3" and "v0.3.3.0" compare as equal and "v0.3.3.1" as newer.
        int Part(int group) => match.Groups[group].Success ? int.Parse(match.Groups[group].Value) : 0;
        version = match.Success ? new Version(Part(1), Part(2), Part(3), Part(4)) : new Version(0, 0, 0, 0);
        return match.Success;
    }

    private static HttpClient CreateClient()
    {
        var client = new HttpClient { Timeout = TimeSpan.FromSeconds(60) };
        client.DefaultRequestHeaders.UserAgent.ParseAdd($"Circularity/{CurrentVersion}");
        client.DefaultRequestHeaders.Accept.ParseAdd("application/vnd.github+json");
        return client;
    }

    [GeneratedRegex(@"^v?(\d+)\.(\d+)(?:\.(\d+))?(?:\.(\d+))?$")]
    private static partial Regex VersionTag();

    [GeneratedRegex(@"^CircularitySetup-.*\.exe$", RegexOptions.IgnoreCase)]
    private static partial Regex InstallerName();
}
