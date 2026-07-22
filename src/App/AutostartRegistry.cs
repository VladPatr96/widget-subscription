using Microsoft.Win32;

namespace WidgetSubscription.App;

/// <summary>
/// The per-user "run at login" registry entry (#5, #16 §autostart): <c>HKCU\Software\Microsoft\
/// Windows\CurrentVersion\Run</c>, value <c>WidgetSubscription</c>. The installer writes it on by
/// default; the tray "Запускать при входе" toggle flips it here. Per-user needs no admin. All calls
/// are guarded for Windows so the cross-platform assembly stays analyzer-clean (CA1416).
/// </summary>
public static class AutostartRegistry
{
    private const string RunKey = @"Software\Microsoft\Windows\CurrentVersion\Run";
    private const string ValueName = "WidgetSubscription";

    public static bool IsEnabled()
    {
        if (!OperatingSystem.IsWindows())
            return false;
        using var key = Registry.CurrentUser.OpenSubKey(RunKey);
        return key?.GetValue(ValueName) is string value && value.Length > 0;
    }

    public static void SetEnabled(bool enabled)
    {
        if (!OperatingSystem.IsWindows())
            return;
        using var key = Registry.CurrentUser.CreateSubKey(RunKey);
        if (key is null)
            return;

        if (enabled)
        {
            var exe = Environment.ProcessPath;
            if (!string.IsNullOrEmpty(exe))
                key.SetValue(ValueName, $"\"{exe}\"");
        }
        else
        {
            key.DeleteValue(ValueName, throwOnMissingValue: false);
        }
    }
}
