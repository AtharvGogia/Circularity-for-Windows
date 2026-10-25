using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Text;
using System.Threading.Tasks;
using Circularity.Engine;
using Microsoft.Win32;

namespace Circularity;

/// <summary>One line of the diagnostics list: what was looked for, whether it's usable, and the details.</summary>
internal sealed record DiagnosticItem(string Name, bool Ok, string Detail);

/// <summary>
/// What Circularity found on this PC (FFmpeg, Office, WebView2, PDFium...), for the About tab in
/// Settings and for "Copy diagnostics", which is what to paste into a bug report.
/// </summary>
internal static class Diagnostics
{
    public static Task<List<DiagnosticItem>> CollectAsync(FfmpegConverter? ffmpeg) => Task.Run(() => Collect(ffmpeg));

    private static List<DiagnosticItem> Collect(FfmpegConverter? ffmpeg)
    {
        var items = new List<DiagnosticItem>
        {
            new("Circularity", true, $"{AppVersion()} in {AppContext.BaseDirectory.TrimEnd('\\')}"),
            new("Windows", true, WindowsVersion()),
        };

        string? ffmpegPath = ffmpeg?.FfmpegPath;
        items.Add(ffmpegPath == null
            ? new("FFmpeg", false, "Not found. Video and audio conversions won't work.")
            : new("FFmpeg", true, $"{FfmpegVersion(ffmpegPath)}, H.264 via {(ffmpeg!.UsesWindowsH264 ? "Windows Media Foundation" : "x264")}, at {ffmpegPath}"));

        var office = new[] { ("Word", "Word.Application"), ("Excel", "Excel.Application"), ("PowerPoint", "PowerPoint.Application") }
            .Select(app => (app.Item1, Found: Type.GetTypeFromProgID(app.Item2) != null))
            .ToList();
        string apps = string.Join(", ", office.Select(a => $"{a.Item1} {(a.Found ? "found" : "missing")}"));
        items.Add(new("Microsoft Office", office.Any(a => a.Found),
            office.Any(a => a.Found) ? $"{apps} ({OfficeProduct()})" : "Not found. Word, Excel and PowerPoint files can't be converted."));

        string? webView = WebView2Version();
        items.Add(webView == null
            ? new("WebView2", false, "Not found. Markdown, HTML and text to PDF won't work.")
            : new("WebView2", true, webView));

        string? pdfium = PdfiumVersion();
        items.Add(pdfium == null
            ? new("PDFium", false, "pdfium.dll is missing. PDF to image won't work; reinstalling should fix it.")
            : new("PDFium", true, pdfium));

        items.Add(new("Settings", true, Settings.FilePath));
        items.Add(new("Log", true, Log.FilePath));
        return items;
    }

    /// <summary>The list as text, followed by the end of the log: what to paste into a bug report.</summary>
    public static string Report(IEnumerable<DiagnosticItem> items, int logLines = 200)
    {
        var text = new StringBuilder();
        foreach (var item in items)
            text.AppendLine($"{item.Name}: {item.Detail}");
        text.AppendLine();
        text.AppendLine($"Last {logLines} lines of the log:");
        foreach (string line in TailOfLog(logLines))
            text.AppendLine(line);
        return text.ToString();
    }

    private static IEnumerable<string> TailOfLog(int count)
    {
        try
        {
            // Opened for sharing, since the app keeps appending to it.
            using var stream = new FileStream(Log.FilePath, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
            using var reader = new StreamReader(stream);
            var lines = new Queue<string>();
            while (reader.ReadLine() is { } line)
            {
                lines.Enqueue(line);
                if (lines.Count > count)
                    lines.Dequeue();
            }
            return lines;
        }
        catch (IOException)
        {
            return new[] { "(The log couldn't be read.)" };
        }
    }

    public static string AppVersion()
    {
        var version = typeof(Diagnostics).Assembly.GetName().Version;
        return version is { Build: > 0 } ? $"{version.Major}.{version.Minor}.{version.Build}" : $"{version?.Major}.{version?.Minor}";
    }

    private static string WindowsVersion()
    {
        using var key = Registry.LocalMachine.OpenSubKey(@"SOFTWARE\Microsoft\Windows NT\CurrentVersion");
        string? name = key?.GetValue("ProductName") as string;
        string? release = key?.GetValue("DisplayVersion") as string;
        // ProductName still says "Windows 10" on Windows 11; the build number tells them apart.
        int build = Environment.OSVersion.Version.Build;
        if (name != null && build >= 22000)
            name = name.Replace("Windows 10", "Windows 11");
        return $"{name ?? "Windows"} {release} (build {build}, {RuntimeInformation.OSArchitecture})".Replace("  ", " ");
    }

    private static string FfmpegVersion(string path)
    {
        try
        {
            var startInfo = new ProcessStartInfo(path) { UseShellExecute = false, CreateNoWindow = true, RedirectStandardOutput = true };
            startInfo.ArgumentList.Add("-version");
            using var process = Process.Start(startInfo)!;
            string first = process.StandardOutput.ReadLine() ?? "";
            process.StandardOutput.ReadToEnd();
            process.WaitForExit();
            // "ffmpeg version 7.1 Copyright (c) ..." → "FFmpeg 7.1"
            var parts = first.Split(' ');
            return parts.Length > 2 && parts[1] == "version" ? $"FFmpeg {parts[2]}" : "FFmpeg";
        }
        catch (Exception ex) when (ex is System.ComponentModel.Win32Exception or IOException)
        {
            return "FFmpeg (couldn't be run)";
        }
    }

    private static string OfficeProduct()
    {
        using var key = Registry.LocalMachine.OpenSubKey(@"SOFTWARE\Microsoft\Office\ClickToRun\Configuration");
        if (key?.GetValue("ProductReleaseIds") is string products)
        {
            // e.g. "OneNoteFreeRetail,HomeStudent2021Retail": the suite is what matters here.
            string main = products.Split(',').FirstOrDefault(p => !p.StartsWith("OneNote", StringComparison.OrdinalIgnoreCase)) ?? products;
            return $"{main}, version {key.GetValue("VersionToReport")}";
        }
        return "installed with Windows Installer";
    }

    private static string? WebView2Version()
    {
        try
        {
            string version = Microsoft.Web.WebView2.Core.CoreWebView2Environment.GetAvailableBrowserVersionString();
            return string.IsNullOrEmpty(version) ? null : version;
        }
        catch (Microsoft.Web.WebView2.Core.WebView2RuntimeNotFoundException)
        {
            return null;
        }
    }

    private static string? PdfiumVersion()
    {
        // Next to the app once published; under runtimes\ in a development build.
        string[] candidates =
        {
            Path.Combine(AppContext.BaseDirectory, "pdfium.dll"),
            Path.Combine(AppContext.BaseDirectory, "runtimes", "win-x64", "native", "pdfium.dll"),
        };
        string? path = candidates.FirstOrDefault(File.Exists);
        return path == null ? null : $"{FileVersionInfo.GetVersionInfo(path).FileVersion}";
    }
}
