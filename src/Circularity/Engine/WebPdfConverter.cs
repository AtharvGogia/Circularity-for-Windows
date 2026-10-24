using System;
using System.Globalization;
using System.IO;
using System.Net;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using System.Windows.Interop;
using System.Windows.Threading;
using Markdig;
using Microsoft.Web.WebView2.Core;

namespace Circularity.Engine;

/// <summary>
/// Markdown, HTML and plain text to PDF, by laying the page out in Edge (WebView2, built into
/// Windows 11) and using its "save as PDF". Markdown can also become a standalone HTML page.
/// </summary>
internal sealed class WebPdfConverter : IConverter, IDisposable
{
    private static readonly MarkdownPipeline Pipeline = new MarkdownPipelineBuilder().UseAdvancedExtensions().Build();

    // Clean, readable defaults for documents that bring no styling of their own.
    private const string Css = """
        body { font-family: "Segoe UI", system-ui, sans-serif; font-size: 11pt; line-height: 1.55; color: #1f2328; max-width: 46em; margin: 0 auto; }
        h1, h2, h3 { line-height: 1.25; margin: 1.4em 0 0.5em; } h1 { font-size: 1.9em; } h2 { font-size: 1.45em; } h3 { font-size: 1.2em; }
        code, pre { font-family: "Cascadia Mono", Consolas, monospace; font-size: 0.92em; }
        pre { background: #f6f8fa; padding: 10px 12px; border-radius: 6px; white-space: pre-wrap; }
        :not(pre) > code { background: #eff1f3; padding: 1px 4px; border-radius: 4px; }
        table { border-collapse: collapse; } th, td { border: 1px solid #d0d7de; padding: 4px 10px; }
        blockquote { color: #59636e; border-left: 4px solid #d0d7de; margin-left: 0; padding-left: 1em; }
        img { max-width: 100%; }
        pre.plain { background: none; padding: 0; font-size: 10pt; }
        """;

    private readonly Lazy<WebViewPrinter> _printer = new(() => new WebViewPrinter());

    /// <summary>Whether the WebView2 runtime (part of Edge) is installed.</summary>
    public static bool IsAvailable
    {
        get
        {
            try
            {
                return !string.IsNullOrEmpty(CoreWebView2Environment.GetAvailableBrowserVersionString());
            }
            catch (WebView2RuntimeNotFoundException)
            {
                return false;
            }
        }
    }

    private readonly bool _available = IsAvailable;

    public bool CanHandle(string inputPath, Operation operation)
    {
        string ext = Formats.NormalizedExtension(inputPath);
        return operation switch
        {
            ConvertTo { Target: "html" } => ext == "md",
            ConvertTo { Target: "pdf" } => _available && ext is "md" or "html" or "txt",
            _ => false,
        };
    }

    public string OutputExtension(string inputPath, Operation operation) => ((ConvertTo)operation).Target;

    public async Task<string> RunAsync(string inputPath, Operation operation, string outputPath, IProgress<double> progress, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        string ext = Formats.NormalizedExtension(inputPath);
        string target = ((ConvertTo)operation).Target;

        if (target == "html")
        {
            await File.WriteAllTextAsync(outputPath, await ToHtmlAsync(inputPath, ext, cancellationToken), new UTF8Encoding(false), cancellationToken);
            progress.Report(1);
            return outputPath;
        }

        // HTML files are printed as they are, so their own styles and relative images work. Markdown
        // and text are turned into a temporary page whose <base> points back at the source folder.
        string? temporary = null;
        try
        {
            string page = inputPath;
            if (ext != "html")
            {
                temporary = Path.Combine(Path.GetTempPath(), $"Circularity-print-{Guid.NewGuid():N}.html");
                await File.WriteAllTextAsync(temporary, await ToHtmlAsync(inputPath, ext, cancellationToken), new UTF8Encoding(false), cancellationToken);
                page = temporary;
            }
            progress.Report(0.2);
            await _printer.Value.PrintToPdfAsync(new Uri(page), outputPath, cancellationToken);
        }
        finally
        {
            if (temporary != null)
                File.Delete(temporary);
        }
        progress.Report(1);
        return outputPath;
    }

    private static async Task<string> ToHtmlAsync(string inputPath, string ext, CancellationToken cancellationToken)
    {
        string text = await File.ReadAllTextAsync(inputPath, cancellationToken);
        string body = ext == "md"
            ? Markdown.ToHtml(text, Pipeline)
            : $"<pre class=\"plain\">{WebUtility.HtmlEncode(text)}</pre>";
        string title = WebUtility.HtmlEncode(Path.GetFileNameWithoutExtension(inputPath));
        string baseHref = new Uri(Path.GetDirectoryName(inputPath)! + Path.DirectorySeparatorChar).AbsoluteUri;
        return $"<!doctype html><html><head><meta charset=\"utf-8\"><title>{title}</title>" +
               $"<base href=\"{baseHref}\"><style>{Css}</style></head><body>{body}</body></html>";
    }

    public void Dispose()
    {
        if (_printer.IsValueCreated)
            _printer.Value.Dispose();
    }

    /// <summary>
    /// One hidden WebView2 on its own UI thread, reused for every print. WebView2 needs a window and a
    /// message loop, and the app's own UI thread shouldn't be held up by page loads.
    /// </summary>
    private sealed class WebViewPrinter : IDisposable
    {
        private readonly SemaphoreSlim _oneAtATime = new(1, 1);
        private readonly TaskCompletionSource<Dispatcher> _dispatcher = new(TaskCreationOptions.RunContinuationsAsynchronously);
        private HwndSource? _host;
        private CoreWebView2Controller? _controller;

        public WebViewPrinter()
        {
            var thread = new Thread(() =>
            {
                _dispatcher.SetResult(Dispatcher.CurrentDispatcher);
                Dispatcher.Run();
            })
            {
                IsBackground = true,
                Name = "WebViewPrinter",
            };
            thread.SetApartmentState(ApartmentState.STA);
            thread.Start();
        }

        public async Task PrintToPdfAsync(Uri page, string outputPath, CancellationToken cancellationToken)
        {
            await _oneAtATime.WaitAsync(cancellationToken);
            try
            {
                var dispatcher = await _dispatcher.Task;
                await await dispatcher.InvokeAsync(() => PrintOnOwnThreadAsync(page, outputPath));
            }
            finally
            {
                _oneAtATime.Release();
            }
        }

        private async Task PrintOnOwnThreadAsync(Uri page, string outputPath)
        {
            if (_controller == null)
            {
                string dataFolder = Path.Combine(
                    Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Circularity", "WebView2");
                var environment = await CoreWebView2Environment.CreateAsync(null, dataFolder);
                // A never-shown window to host the browser; printing doesn't need it on screen.
                _host = new HwndSource(new HwndSourceParameters("Circularity print host") { Width = 800, Height = 600, WindowStyle = 0 });
                _controller = await environment.CreateCoreWebView2ControllerAsync(_host.Handle);
                _controller.IsVisible = false;
            }

            var web = _controller.CoreWebView2;
            var loaded = new TaskCompletionSource<CoreWebView2WebErrorStatus?>();
            void OnCompleted(object? sender, CoreWebView2NavigationCompletedEventArgs e)
            {
                web.NavigationCompleted -= OnCompleted;
                loaded.TrySetResult(e.IsSuccess ? null : e.WebErrorStatus);
            }
            web.NavigationCompleted += OnCompleted;
            web.Navigate(page.AbsoluteUri);
            if (await loaded.Task is { } error)
                throw new InvalidOperationException($"Couldn't lay out the page ({error}).");

            var settings = web.Environment.CreatePrintSettings();
            settings.ShouldPrintBackgrounds = true;
            settings.ShouldPrintHeaderAndFooter = false;
            settings.MarginTop = settings.MarginBottom = settings.MarginLeft = settings.MarginRight = 0.6;
            // A4 where the metric system is used, US Letter elsewhere (sizes in inches).
            if (RegionInfo.CurrentRegion.IsMetric)
            {
                settings.PageWidth = 8.27;
                settings.PageHeight = 11.69;
            }

            File.Delete(outputPath);
            if (!await web.PrintToPdfAsync(outputPath, settings))
                throw new InvalidOperationException("Edge couldn't save the page as a PDF.");
        }

        public void Dispose()
        {
            if (_dispatcher.Task.IsCompletedSuccessfully)
            {
                var dispatcher = _dispatcher.Task.Result;
                dispatcher.Invoke(() =>
                {
                    _controller?.Close();
                    _host?.Dispose();
                });
                dispatcher.InvokeShutdown();
            }
        }
    }
}
