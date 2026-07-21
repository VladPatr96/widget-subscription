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
