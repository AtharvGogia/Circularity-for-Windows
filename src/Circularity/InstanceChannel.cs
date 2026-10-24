using System;
using System.Collections.Generic;
using System.IO;
using System.IO.Pipes;
using System.Security.AccessControl;
using System.Security.Principal;
using System.Text;
using System.Threading;
using System.Threading.Tasks;

namespace Circularity;

/// <summary>
/// Only one Circularity runs at a time. When it's launched again, e.g. by "Convert with Circularity"
/// in Explorer's menu, Send to, or the Start menu shortcut, the new copy passes its files (or a
/// request to open Settings) to the running one over a named pipe that only this user can open,
/// then exits.
/// </summary>
internal sealed class InstanceChannel : IDisposable
{
    /// <summary>Sent instead of file paths when launched with nothing to convert.</summary>
    public const string OpenSettingsMessage = "::settings";

    private static readonly SecurityIdentifier CurrentUser = WindowsIdentity.GetCurrent().User!;
    private static readonly string PipeName = "Circularity-" + CurrentUser.Value;

    private readonly CancellationTokenSource _stop = new();

    /// <summary>Raised on a background thread with the lines another launch sent.</summary>
    public event Action<string[]>? Received;

    public void StartListening() => Task.Run(ListenAsync);

    private async Task ListenAsync()
    {
        while (!_stop.IsCancellationRequested)
        {
            try
            {
                var security = new PipeSecurity();
                security.AddAccessRule(new PipeAccessRule(CurrentUser, PipeAccessRights.ReadWrite, AccessControlType.Allow));
                await using var server = NamedPipeServerStreamAcl.Create(
                    PipeName, PipeDirection.In, 1, PipeTransmissionMode.Byte, PipeOptions.Asynchronous, 0, 0, security);
                await server.WaitForConnectionAsync(_stop.Token);

                using var reader = new StreamReader(server, Encoding.UTF8);
                string text = await reader.ReadToEndAsync(_stop.Token);
                string[] lines = text.Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
                if (lines.Length > 0)
                    Received?.Invoke(lines);
            }
            catch (OperationCanceledException)
            {
                break;
            }
            catch (IOException ex)
            {
                Log.Write($"Instance channel: {ex.Message}");
            }
        }
    }

    /// <summary>Sends lines to the running copy. Returns false if none is listening.</summary>
    public static bool TrySend(IEnumerable<string> lines)
    {
        try
        {
            using var client = new NamedPipeClientStream(".", PipeName, PipeDirection.Out);
            // Explorer starts one copy per selected file, so several may queue up for the pipe.
            client.Connect(5000);
            using var writer = new StreamWriter(client, new UTF8Encoding(false));
            foreach (string line in lines)
                writer.WriteLine(line);
            writer.Flush();
            return true;
        }
        catch (Exception ex) when (ex is TimeoutException or IOException or UnauthorizedAccessException)
        {
            return false;
        }
    }

    public void Dispose() => _stop.Cancel();
}
