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
