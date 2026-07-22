using System.Diagnostics;

namespace WidgetSubscription.Providers.ClaudeCode;

/// <summary>
/// Opens URLs in the user's default browser via the OS shell (#18 §2). Reuses the browser's
/// claude.ai session and adds no WebView dependency, keeping the app a self-contained single file (#16).
/// </summary>
public sealed class SystemBrowserLauncher : IBrowserLauncher
{
    public void Open(string url)
        => Process.Start(new ProcessStartInfo(url) { UseShellExecute = true });
}
