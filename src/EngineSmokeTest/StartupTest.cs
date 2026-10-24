using System;
using Circularity;
using Microsoft.Win32;

namespace EngineSmokeTest;

/// <summary>
/// Exercises the "start when I sign in" toggle against the real registry, then puts back exactly
/// what was there before (including any Task Manager enabled/disabled flag).
/// </summary>
internal static class StartupTest
{
    private const string RunKey = @"Software\Microsoft\Windows\CurrentVersion\Run";
    private const string ApprovedKey = @"Software\Microsoft\Windows\CurrentVersion\Explorer\StartupApproved\Run";
    private const string Name = "Circularity";

    public static int Run()
    {
        object? originalRun = Read(RunKey);
        object? originalApproved = Read(ApprovedKey);
        int failures = 0;
        void Check(string name, bool ok)
        {
            Console.WriteLine($"{(ok ? "PASS" : "FAIL")}  {name}");
            if (!ok)
                failures++;
        }

        try
        {
            const string exe = @"C:\Test\Circularity.exe";
            StartAtSignIn.Set(true, exe);
            Check("turning on adds the sign-in entry", StartAtSignIn.IsEnabled && (string?)Read(RunKey) == $"\"{exe}\"");

            // What Task Manager writes when you click "Disable" on Startup apps.
            using (var approved = Registry.CurrentUser.CreateSubKey(ApprovedKey))
                approved.SetValue(Name, new byte[] { 3, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0 }, RegistryValueKind.Binary);
            Check("disabled in Task Manager shows as off", !StartAtSignIn.IsEnabled);

            StartAtSignIn.Set(true, exe);
            Check("turning on again clears Task Manager's 'disabled'", StartAtSignIn.IsEnabled && Read(ApprovedKey) == null);

            StartAtSignIn.Set(false);
            Check("turning off removes the entry", !StartAtSignIn.IsEnabled && Read(RunKey) == null);
        }
        finally
        {
            Restore(RunKey, originalRun, RegistryValueKind.String);
            Restore(ApprovedKey, originalApproved, RegistryValueKind.Binary);
        }

        bool restored = Equals(Read(RunKey), originalRun) &&
            (originalApproved is byte[] before ? Read(ApprovedKey) is byte[] after && after.AsSpan().SequenceEqual(before) : Read(ApprovedKey) == null);
        Check("registry put back as it was", restored);
        return failures == 0 ? 0 : 1;
    }

    private static object? Read(string key)
    {
        using var k = Registry.CurrentUser.OpenSubKey(key);
        return k?.GetValue(Name);
    }

    private static void Restore(string key, object? value, RegistryValueKind kind)
    {
        using var k = Registry.CurrentUser.CreateSubKey(key);
        if (value == null)
            k.DeleteValue(Name, throwOnMissingValue: false);
        else
            k.SetValue(Name, value, kind);
    }
}
