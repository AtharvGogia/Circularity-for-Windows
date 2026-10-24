using System;
using System.IO;
using System.Net;
using System.Net.Http;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Circularity;

namespace EngineSmokeTest;

/// <summary>
/// Checks the updater against fake GitHub responses (newer / same version / no releases), its download
/// safety checks, and finally asks the real GitHub API once, reporting whatever it says.
/// </summary>
internal static class UpdateTest
{
    private const string Api = "https://api.github.com/repos/test/test/releases/latest";

    public static async Task<int> Run(string? realInstaller)
    {
        int failures = 0;
        void Check(string name, bool ok, string detail = "")
        {
            Console.WriteLine($"{(ok ? "PASS" : "FAIL")}  {name,-48} {detail}");
            if (!ok)
                failures++;
        }

        var current = UpdateChecker.CurrentVersion;
        var newer = new Version(current.Major, current.Minor + 1, 0);
        string installerUrl = $"https://github.com/test/test/releases/download/v{newer}/CircularitySetup-{newer}.exe";

        // A newer release with an installer attached.
        var latest = await Checker(_ => Json(Release($"v{newer}", installerUrl))).GetLatestAsync();
        Check("newer release is offered", latest is { IsNewer: true } && latest.InstallerUrl == installerUrl, $"{latest?.Version}");

        // The same version.
        latest = await Checker(_ => Json(Release($"v{current}", installerUrl))).GetLatestAsync();
        Check("same version isn't offered", latest is { IsNewer: false }, $"{latest?.Version}");

        // Nothing published yet (or the repository is private).
        latest = await Checker(_ => new HttpResponseMessage(HttpStatusCode.NotFound)).GetLatestAsync();
        Check("no releases is reported as none", latest == null);

        // Downloads only from github.com.
        var elsewhere = new UpdateInfo(newer, "v", "", "https://example.com/CircularitySetup.exe", "");
        bool refused = await Throws(() => Checker(_ => Bytes(new byte[] { 0x4D, 0x5A })).DownloadInstallerAsync(elsewhere, null));
        Check("downloads from other sites are refused", refused);

        // A file that isn't our installer is rejected and deleted.
        var update = new UpdateInfo(newer, $"v{newer}", "", installerUrl, "");
        var fake = Encoding.ASCII.GetBytes("MZ this is not an installer");
        string fakePath = Path.Combine(Path.GetTempPath(), "Circularity-update", $"CircularitySetup-{newer}.exe");
        bool rejected = await Throws(() => Checker(_ => Bytes(fake)).DownloadInstallerAsync(update, null));
        Check("a fake installer is rejected and deleted", rejected && !File.Exists(fakePath));

        // The real installer passes the check.
        if (realInstaller != null && File.Exists(realInstaller))
        {
            double lastProgress = 0;
            string path = await Checker(_ => Bytes(File.ReadAllBytes(realInstaller)))
                .DownloadInstallerAsync(update, new SyncProgress(p => lastProgress = p));
            Check("a real Circularity installer is accepted", File.Exists(path) && UpdateChecker.LooksLikeOurInstaller(path),
                $"progress reached {lastProgress:P0}");
            File.Delete(path);
        }
        else
        {
            Console.WriteLine("skip  real installer check (build the installer first)");
        }

        // The real thing, for information only.
        try
        {
            var live = await new UpdateChecker().GetLatestAsync();
            Console.WriteLine(live == null
                ? $"info  GitHub ({UpdateChecker.Repository}): no public releases yet"
                : $"info  GitHub ({UpdateChecker.Repository}): latest is {live.Version}, installer {(live.InstallerUrl != null ? "attached" : "missing")}");
        }
        catch (Exception ex)
        {
            Console.WriteLine($"info  GitHub check failed: {ex.Message}");
        }

        return failures == 0 ? 0 : 1;
    }

    private static UpdateChecker Checker(Func<HttpRequestMessage, HttpResponseMessage> respond) =>
        new(new HttpClient(new FakeHandler(respond)), Api);

    private static string Release(string tag, string installerUrl) => $$"""
        {
          "tag_name": "{{tag}}",
          "html_url": "https://github.com/test/test/releases/tag/{{tag}}",
          "body": "What's new",
          "assets": [
            { "name": "notes.txt", "browser_download_url": "https://github.com/test/test/notes.txt" },
            { "name": "{{Path.GetFileName(installerUrl)}}", "browser_download_url": "{{installerUrl}}" }
          ]
        }
        """;

    private static HttpResponseMessage Json(string body) =>
        new(HttpStatusCode.OK) { Content = new StringContent(body, Encoding.UTF8, "application/json") };

    private static HttpResponseMessage Bytes(byte[] body) =>
        new(HttpStatusCode.OK) { Content = new ByteArrayContent(body) };

    private static async Task<bool> Throws(Func<Task> action)
    {
        try
        {
            await action();
            return false;
        }
        catch (InvalidOperationException)
        {
            return true;
        }
    }

    private sealed class FakeHandler(Func<HttpRequestMessage, HttpResponseMessage> respond) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken) =>
            Task.FromResult(respond(request));
    }

    private sealed class SyncProgress(Action<double> report) : IProgress<double>
    {
        public void Report(double value) => report(value);
    }
}
