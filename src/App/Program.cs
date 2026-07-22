using Avalonia;

namespace WidgetSubscription.App;

internal static class Program
{
    // Initialization code. Don't use any Avalonia, third-party APIs or any
    // SynchronizationContext-reliant code before AppMain is called: things aren't
    // initialized yet and stuff might break.
    [STAThread]
    public static int Main(string[] args)
    {
        // Headless end-to-end smoke check, no window/message loop.
        if (args.Contains("--selftest"))
            return SelfTest.RunAsync().GetAwaiter().GetResult();

        // Console harness to diagnose the interactive login against live endpoints.
        if (args.Contains("--login"))
            return LoginDiagnostic.RunAsync().GetAwaiter().GetResult();

        // Regenerate the committed app icon from the SkiaSharp geometry (build asset, run once).
        if (args is ["--makeicon", var iconPath, ..])
        {
            File.WriteAllBytes(iconPath, AppIconRenderer.BuildIco(16, 24, 32, 48, 64, 128, 256));
            Console.WriteLine($"icon written: {iconPath}");
            return 0;
        }

        // One widget per user: a second instance would double the poll rate against the shared
        // usage endpoint and invite HTTP 429 rate-limits, so bail if we are not the first.
        using var single = new System.Threading.Mutex(initiallyOwned: true, @"Local\WidgetSubscription.SingleInstance", out var isFirst);
        if (!isFirst)
            return 0;

        BuildAvaloniaApp().StartWithClassicDesktopLifetime(args);
        return 0;
    }

    // Avalonia configuration, don't remove; also used by the visual designer.
    public static AppBuilder BuildAvaloniaApp()
        => AppBuilder.Configure<App>()
            .UsePlatformDetect()
            .WithInterFont()
            .LogToTrace();
}
