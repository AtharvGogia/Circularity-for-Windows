using System;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Threading;
using System.Threading.Tasks;

namespace Circularity.Engine;

/// <summary>
/// Office documents through the user's own Microsoft Office, when it's installed: the best possible
/// fidelity, because it's the real Word / Excel / PowerPoint doing the work, invisibly.
///
///  - Word:       DOCX / DOC / RTF / ODT → PDF, DOCX, TXT
///  - Excel:      XLSX / XLS / ODS / CSV → PDF
///  - PowerPoint: PPTX / PPT / ODP → PDF
///
/// Office may hand us a copy the user already has open (PowerPoint always does) instead of a new
/// hidden one. So we only close the app afterwards if it had no documents open when we started,
/// and any setting we change on it is put back.
///
/// An invisible Office app that shows a prompt waits forever, so: documents are opened with a dummy
/// password (a protected file fails at once instead of asking for one), and a watchdog ends the copy
/// of Office we started if it takes too long or the job is cancelled.
/// </summary>
internal sealed class OfficeConverter : IConverter
{
    // Office's own constants.
    private const int WdFormatText = 2, WdFormatDocx = 16, WdFormatPdf = 17, WdDoNotSaveChanges = 0, WdAlertsNone = 0;
    private const int XlTypePdf = 0;
    private const int PpSaveAsPdf = 32, MsoTrue = -1, MsoFalse = 0;
    private const int Utf8CodePage = 65001;
    private const string NoPassword = "Circularity-not-a-password";

    private static readonly TimeSpan Timeout = TimeSpan.FromMinutes(3);

    private readonly bool _hasWord = Installed("Word.Application");
    private readonly bool _hasExcel = Installed("Excel.Application");
    private readonly bool _hasPowerPoint = Installed("PowerPoint.Application");

    public bool CanHandle(string inputPath, Operation operation)
    {
        if (operation is not ConvertTo { Target: var target })
            return false;
        string ext = Formats.NormalizedExtension(inputPath);
        return ext switch
        {
            "docx" or "doc" or "rtf" or "odt" => _hasWord && (target is "pdf" or "txt" || (target == "docx" && ext != "docx")),
            "xlsx" or "xls" or "ods" or "csv" => _hasExcel && target == "pdf",
            "pptx" or "ppt" or "odp" => _hasPowerPoint && target == "pdf",
            _ => false,
        };
    }

    public string OutputExtension(string inputPath, Operation operation) => ((ConvertTo)operation).Target;

    public async Task<string> RunAsync(string inputPath, Operation operation, string outputPath, IProgress<double> progress, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        string target = ((ConvertTo)operation).Target;
        string ext = Formats.NormalizedExtension(inputPath);
        var session = new Session();

        var work = Sta.Run(() =>
        {
            // Office won't always overwrite, and the placeholder is empty anyway.
            File.Delete(outputPath);
            switch (ext)
            {
                case "xlsx" or "xls" or "ods" or "csv":
                    WithExcel(session, inputPath, outputPath);
                    break;
                case "pptx" or "ppt" or "odp":
                    WithPowerPoint(session, inputPath, outputPath);
                    break;
                default:
                    WithWord(session, inputPath, outputPath, target);
                    break;
            }
            return 0;
        }, "OfficeConverter");

        var finished = await Task.WhenAny(work, Task.Delay(Timeout, cancellationToken));
        if (finished != work)
        {
            // Stuck or cancelled. Ending the process makes the waiting call on the Office thread fail,
            // which ends that thread too; its error is no longer interesting.
            session.KillIfOurs();
            _ = work.ContinueWith(t => _ = t.Exception, TaskScheduler.Default);
            cancellationToken.ThrowIfCancellationRequested();
            throw new TimeoutException($"{session.AppName} didn't finish within {Timeout.TotalMinutes:0} minutes, so it was stopped.");
        }
        await work;

        progress.Report(1);
        return outputPath;
    }

    private static void WithWord(Session session, string inputPath, string outputPath, string target)
    {
        dynamic word = session.Start("Word.Application", "WINWORD");
        bool wasIdle = word.Documents.Count == 0;
        session.OursToEnd = wasIdle;
        int alerts = word.DisplayAlerts;
        try
        {
            word.DisplayAlerts = WdAlertsNone;
            // FileName, ConfirmConversions, ReadOnly, AddToRecentFiles, PasswordDocument
            dynamic document = word.Documents.Open(inputPath, false, true, false, NoPassword);
            try
            {
                if (target == "txt")
                {
                    // Encoding is SaveAs2's 12th parameter.
                    var missing = Type.Missing;
                    document.SaveAs2(outputPath, WdFormatText, missing, missing, false, missing, missing, missing, missing, missing, missing, Utf8CodePage);
                }
                else
                {
                    document.SaveAs2(outputPath, target == "pdf" ? WdFormatPdf : WdFormatDocx);
                }
            }
            finally
            {
                document.Close(WdDoNotSaveChanges);
                Release(document);
            }
        }
        finally
        {
            word.DisplayAlerts = alerts;
            if (wasIdle)
                word.Quit(WdDoNotSaveChanges);
            Release(word);
        }
    }

    private static void WithExcel(Session session, string inputPath, string outputPath)
    {
        dynamic excel = session.Start("Excel.Application", "EXCEL");
        bool wasIdle = excel.Workbooks.Count == 0;
        session.OursToEnd = wasIdle;
        bool alerts = excel.DisplayAlerts;
        try
        {
            excel.DisplayAlerts = false;
            // FileName, UpdateLinks (0 = don't), ReadOnly, Format, Password
            dynamic workbook = excel.Workbooks.Open(inputPath, 0, true, Type.Missing, NoPassword);
            try
            {
                workbook.ExportAsFixedFormat(XlTypePdf, outputPath);
            }
            finally
            {
                workbook.Close(false);
                Release(workbook);
            }
        }
        finally
        {
            excel.DisplayAlerts = alerts;
            if (wasIdle)
                excel.Quit();
            Release(excel);
        }
    }

    private static void WithPowerPoint(Session session, string inputPath, string outputPath)
    {
        dynamic powerPoint = session.Start("PowerPoint.Application", "POWERPNT");
        bool wasIdle = powerPoint.Presentations.Count == 0;
        session.OursToEnd = wasIdle;
        try
        {
            // FileName, ReadOnly, Untitled, WithWindow (no window, so nothing flashes on screen).
            // "::password::" on the end makes a protected file fail instead of prompting.
            dynamic presentation = powerPoint.Presentations.Open($"{inputPath}::{NoPassword}::", MsoTrue, MsoFalse, MsoFalse);
            try
            {
                presentation.SaveAs(outputPath, PpSaveAsPdf);
            }
            finally
            {
                presentation.Close();
                Release(presentation);
            }
        }
        finally
        {
            if (wasIdle)
                powerPoint.Quit();
            Release(powerPoint);
        }
    }

    private static bool Installed(string progId) => Type.GetTypeFromProgID(progId) != null;

    private static void Release(object comObject)
    {
        if (Marshal.IsComObject(comObject))
            Marshal.FinalReleaseComObject(comObject);
    }

    /// <summary>Starts (or attaches to) an Office app and remembers which process, if any, we started.</summary>
    private sealed class Session
    {
        private volatile int _processId;

        public string AppName { get; private set; } = "Office";

        /// <summary>True only when the app had nothing open, i.e. it's ours and safe to end.</summary>
        public volatile bool OursToEnd;

        public dynamic Start(string progId, string processName)
        {
            AppName = progId.Split('.')[0];
            var before = Process.GetProcessesByName(processName).Select(p => p.Id).ToHashSet();
            dynamic app = Activator.CreateInstance(Type.GetTypeFromProgID(progId) ?? throw new InvalidOperationException($"{AppName} isn't installed."))!;
            // A brand-new process is the one we started; if none appeared, we attached to the user's.
            _processId = Process.GetProcessesByName(processName).Select(p => p.Id).FirstOrDefault(id => !before.Contains(id));
            return app;
        }

        public void KillIfOurs()
        {
            if (!OursToEnd || _processId == 0)
                return;
            try
            {
                using var process = Process.GetProcessById(_processId);
                process.Kill();
            }
            catch (Exception ex) when (ex is ArgumentException or InvalidOperationException)
            {
                // Already gone.
            }
        }
    }
}
