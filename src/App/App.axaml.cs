using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Markup.Xaml;
using WidgetSubscription.Core;
using WidgetSubscription.Providers.ClaudeCode;

namespace WidgetSubscription.App;

public partial class App : Application
{
    private HttpClient? _http;
    private UsageMonitor? _monitor;
    private TrayController? _controller;

    public override void Initialize() => AvaloniaXamlLoader.Load(this);

    public override void OnFrameworkInitializationCompleted()
    {
        if (ApplicationLifetime is IClassicDesktopStyleApplicationLifetime desktop)
        {
            // Tray-only app: no main window, so it must not exit when windows close.
            desktop.ShutdownMode = ShutdownMode.OnExplicitShutdown;

            _http = new HttpClient { Timeout = TimeSpan.FromSeconds(15) };

            var tokenStore = new WidgetTokenFileStore();
            var modeStore = new CredentialModeFileStore();
            var borrow = new ClaudeCredentialsFileSource();
            var own = new OwnLoginCredentialSource(_http, tokenStore);
            var credentials = new SelectingCredentialSource(borrow, own, modeStore);
            var provider = new ClaudeCodeAdapter(_http, credentials);
            _monitor = new UsageMonitor(provider);

            IWidgetLogin LoginFactory(ICodeEntry codeEntry) => new WidgetLogin(
                _http!, tokenStore, new SystemBrowserLauncher(), new HttpLoopbackListenerFactory(), codeEntry);

            _controller = new TrayController(_monitor, modeStore, tokenStore, LoginFactory);
            _monitor.Start();

            desktop.Exit += (_, _) =>
            {
                _controller?.Dispose();
                _monitor?.Dispose();
                _http?.Dispose();
            };
        }

        base.OnFrameworkInitializationCompleted();
    }
}
