using System;
using System.IO;
using System.Linq;
using System.Threading;
using System.Windows.Automation;
using Circularity;
using Microsoft.Toolkit.Uwp.Notifications;
using Microsoft.Win32;

namespace EngineSmokeTest;

/// <summary>
/// Shows the real "Done" notification (registered under this test's own exe), presses its Open
/// folder button through UI Automation as a click would, and checks the click comes back with the
/// right action and file; then unregisters and checks nothing is left in the registry. A
/// notification pops up for a few seconds. With Do not disturb on, Windows hides it and the button
/// checks are skipped.
/// </summary>
internal static class NotificationTest
{
    public static int Run()
    {
        int failures = 0;
        void Check(string name, bool ok, string detail = "")
        {
            Console.WriteLine($"{(ok ? "PASS" : "FAIL")}  {name}{(detail.Length > 0 ? "  " + detail : "")}");
            if (!ok)
                failures++;
        }

        string file = Path.Combine(Path.GetTempPath(), $"Circularity notification test {Guid.NewGuid():N}.png");
        File.WriteAllBytes(file, new byte[] { 1 });
        string? gotAction = null, gotPath = null;
        using var clicked = new ManualResetEventSlim();
        FinishedToast.Listen((action, path) =>
        {
            gotAction = action;
            gotPath = path;
            clicked.Set();
        });

        try
        {
            Check("Windows takes the notification", FinishedToast.TryShow("Done", Path.GetFileName(file), file, single: true));
            Thread.Sleep(1500);
            bool inHistory = ToastNotificationManagerCompat.History.GetHistory().Count > 0;
            Check("it's in the notification centre", inHistory);

            var button = FindButton("Open folder", TimeSpan.FromSeconds(8));
            if (button == null)
            {
                // Hidden (a locked PC, Do not disturb): make the very call Windows makes when the
                // button is pressed, with that button's own arguments from the delivered notification.
                Console.WriteLine("note  the notification isn't on screen (locked PC or Do not disturb); pressing its button the way Windows does instead");
                var toast = ToastNotificationManagerCompat.History.GetHistory().FirstOrDefault();
                var xml = toast == null ? null : System.Xml.Linq.XDocument.Parse(toast.Content.GetXml());
                var actions = xml?.Descendants("action").ToList() ?? new();
                Check("it has Open and Open folder buttons", actions.Select(a => (string?)a.Attribute("content")).SequenceEqual(new[] { "Open", "Open folder" }),
                    string.Join(", ", actions.Select(a => (string?)a.Attribute("content"))));
                string? arguments = (string?)actions.FirstOrDefault(a => (string?)a.Attribute("content") == "Open folder")?.Attribute("arguments");
                if (arguments != null && ActivatorFor("EngineSmokeTest") is Guid clsid)
                {
                    var callback = (INotificationActivationCallback)Activator.CreateInstance(Type.GetTypeFromCLSID(clsid)!)!;
                    callback.Activate("", arguments, IntPtr.Zero, 0);
                    bool came = clicked.Wait(TimeSpan.FromSeconds(10));
                    Check("pressing Open folder reaches Circularity", came);
                    Check("with the action and the file", gotAction == FinishedToast.OpenFolder && gotPath == file, $"{gotAction} {gotPath}");
                }
                else
                {
                    Check("Windows knows how to send the click back", false, arguments == null ? "no Open folder button" : "no activator registered");
                }
            }
            else
            {
                Check("it has an Open button too", FindButton("Open", TimeSpan.FromSeconds(1), exact: true) != null);
                ((InvokePattern)button.GetCurrentPattern(InvokePattern.Pattern)).Invoke();
                bool came = clicked.Wait(TimeSpan.FromSeconds(10));
                Check("pressing Open folder reaches Circularity", came);
                Check("with the action and the file", gotAction == FinishedToast.OpenFolder && gotPath == file, $"{gotAction} {gotPath}");
            }
        }
        finally
        {
            ToastNotificationManagerCompat.History.Clear();
            FinishedToast.Unregister();
            File.Delete(file);
        }

        // The toolkit registers under HKCU\Software\Classes\AppUserModelId\<id>, named after this exe.
        using var ids = Registry.CurrentUser.OpenSubKey(@"Software\Classes\AppUserModelId");
        var left = ids?.GetSubKeyNames().Where(n => n.Contains("EngineSmokeTest", StringComparison.OrdinalIgnoreCase)).ToArray() ?? Array.Empty<string>();
        Check("unregistering leaves nothing behind", left.Length == 0, string.Join(", ", left));
        return failures == 0 ? 0 : 1;
    }

    /// <summary>The COM class Windows calls with a click, as registered for this exe's notifications.</summary>
    private static Guid? ActivatorFor(string exeName)
    {
        using var ids = Registry.CurrentUser.OpenSubKey(@"Software\Classes\AppUserModelId");
        foreach (string id in ids?.GetSubKeyNames() ?? Array.Empty<string>())
        {
            using var key = ids!.OpenSubKey(id);
            if (key?.GetValue("CustomActivator") is not string clsid)
                continue;
            using var server = Registry.CurrentUser.OpenSubKey($@"Software\Classes\CLSID\{clsid}\LocalServer32");
            if ((server?.GetValue(null) as string)?.Contains(exeName, StringComparison.OrdinalIgnoreCase) == true)
                return Guid.Parse(clsid);
        }
        return null;
    }

    /// <summary>What Windows calls on the app when a notification (or one of its buttons) is clicked.</summary>
    [System.Runtime.InteropServices.ComImport]
    [System.Runtime.InteropServices.Guid("53E31837-6600-4A81-9395-75CFFE746F94")]
    [System.Runtime.InteropServices.InterfaceType(System.Runtime.InteropServices.ComInterfaceType.InterfaceIsIUnknown)]
    private interface INotificationActivationCallback
    {
        void Activate([System.Runtime.InteropServices.MarshalAs(System.Runtime.InteropServices.UnmanagedType.LPWStr)] string appUserModelId,
            [System.Runtime.InteropServices.MarshalAs(System.Runtime.InteropServices.UnmanagedType.LPWStr)] string invokedArgs,
            IntPtr data, uint count);
    }

    private static AutomationElement? FindButton(string name, TimeSpan timeout, bool exact = false)
    {
        var condition = new AndCondition(
            new PropertyCondition(AutomationElement.ControlTypeProperty, ControlType.Button),
            new PropertyCondition(AutomationElement.NameProperty, name));
        var until = DateTime.Now + timeout;
        do
        {
            // Notifications live in their own top-level window ("New notification").
            foreach (AutomationElement window in AutomationElement.RootElement.FindAll(TreeScope.Children, Condition.TrueCondition))
            {
                string title;
                try
                {
                    title = window.Current.Name;
                }
                catch (ElementNotAvailableException)
                {
                    continue;
                }
                if (!title.Contains("notification", StringComparison.OrdinalIgnoreCase))
                    continue;
                var found = window.FindFirst(TreeScope.Descendants, condition);
                if (found != null)
                    return found;
            }
            Thread.Sleep(250);
        } while (DateTime.Now < until);
        return null;
    }
}
