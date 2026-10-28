using System;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Threading;
using Circularity;

namespace EngineSmokeTest;

/// <summary>
/// Runs the real app and makes it fail on purpose (see App.CrashTestHook), once on its own thread
/// and once on another, checking the safety net: the first is survived and saved; the second is
/// saved, ends the process, restarts it, and the restarted copy picks up the report to offer it.
/// Close Circularity before running it; the copies it starts are closed at the end.
/// </summary>
internal static class CrashTest
{
    public static int Run(string? exePath)
    {
        string exe = exePath ?? Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, @"..\..\..\..\Circularity\bin\Debug\net10.0-windows10.0.19041.0\Circularity.exe"));
        int failures = 0;
        void Check(string name, bool ok, string detail = "")
        {
            Console.WriteLine($"{(ok ? "PASS" : "FAIL")}  {name}{(detail.Length > 0 ? "  " + detail : "")}");
            if (!ok)
                failures++;
        }

        if (!File.Exists(exe))
        {
            Console.WriteLine($"No app at {exe}; build it first or pass its path.");
            return 2;
        }
        if (Process.GetProcessesByName("Circularity").Length > 0)
        {
            Console.WriteLine("Circularity is running; close it first (the test starts its own copies).");
            return 2;
        }
        Directory.CreateDirectory(CrashGuard.Folder);
        string pending = Path.Combine(CrashGuard.Folder, "pending.txt");
        File.Delete(pending);

        try
        {
            // On the app's own thread: recorded, and the app keeps going.
            DateTime before = DateTime.Now.AddSeconds(-1);
            using (var app = Start(exe, "ui"))
            {
                Thread.Sleep(5000);
                Check("an error on the app's thread doesn't close it", !app.HasExited);
                var report = NewReports(before).FirstOrDefault(f => f.Name.Contains("(recovered)"));
                Check("it's saved as a crash report", report != null && File.ReadAllText(report.FullName).Contains("Crash test (app thread)"), report?.Name ?? "none");
                Check("and isn't left for the next start", !File.Exists(pending));
                app.Kill();
                app.WaitForExit();
            }

            // On another thread: .NET ends the process; it's recorded and Circularity starts again.
            before = DateTime.Now.AddSeconds(-1);
            using (var app = Start(exe, "background"))
            {
                bool ended = app.WaitForExit(15000);
                Check("a fatal error on another thread ends that copy", ended);
                var report = NewReports(before).FirstOrDefault(f => !f.Name.Contains("(recovered)"));
                Check("it's saved as a crash report", report != null && File.ReadAllText(report.FullName).Contains("Crash test (background thread)"), report?.Name ?? "none");

                Process? restarted = null;
                for (int i = 0; i < 40 && restarted == null; i++)
                {
                    Thread.Sleep(250);
                    restarted = Process.GetProcessesByName("Circularity").FirstOrDefault(p => p.Id != app.Id);
                }
                Check("Circularity starts again by itself", restarted != null);
                Thread.Sleep(4000);
                Check("the restarted copy keeps running (no crash loop)", restarted != null && !restarted.HasExited);
                Check("and picks up the report to offer it", !File.Exists(pending));
                Check("the report reads back with its kind and message",
                    report != null && CrashReport.Read(report.FullName) is { Kind: "InvalidOperationException", Message: "Crash test (background thread)" });
            }
        }
        finally
        {
            foreach (var p in Process.GetProcessesByName("Circularity"))
            {
                p.Kill();
                p.WaitForExit();
            }
            File.Delete(pending);
            // Only the test's own reports; any real ones stay.
            foreach (var report in new DirectoryInfo(CrashGuard.Folder).GetFiles("crash *.txt").Where(f => File.ReadAllText(f.FullName).Contains("Crash test (")))
                report.Delete();
        }
        return failures == 0 ? 0 : 1;
    }

    private static Process Start(string exe, string mode)
    {
        var info = new ProcessStartInfo(exe) { UseShellExecute = false };
        info.Environment["CIRCULARITY_TEST_CRASH"] = mode;
        return Process.Start(info)!;
    }

    private static FileInfo[] NewReports(DateTime since) =>
        Directory.Exists(CrashGuard.Folder)
            ? new DirectoryInfo(CrashGuard.Folder).GetFiles("crash *.txt").Where(f => f.LastWriteTime >= since).OrderByDescending(f => f.LastWriteTime).ToArray()
            : Array.Empty<FileInfo>();
}
