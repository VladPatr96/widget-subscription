using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Threading;
using WidgetSubscription.Core;

namespace WidgetSubscription.App;

/// <summary>
/// Owns the tray presence and marshals engine updates onto the UI thread. Redraws the donut icon
/// and tooltip on every <see cref="UsageMonitor.Updated"/>, opens the <see cref="PanelWindow"/>
/// (force-refreshing per the open policy) on left-click, and exits from the context menu.
/// </summary>
public sealed class TrayController : IDisposable
{
    private readonly UsageMonitor _monitor;
    private readonly TrayIcon _tray;
    private PanelWindow? _panel;

    public TrayController(UsageMonitor monitor)
    {
        _monitor = monitor;

        var exit = new NativeMenuItem("Выход");
        exit.Click += (_, _) => Shutdown();
        var menu = new NativeMenu();
        menu.Add(exit);

        _tray = new TrayIcon
        {
            ToolTipText = "Widget Subscription",
            IsVisible = true,
            Menu = menu,
        };
        _tray.Clicked += (_, _) => TogglePanel();

        _monitor.Updated += OnUpdated;
        UpdateTray();
    }

    private void OnUpdated(object? sender, EventArgs e) => Dispatcher.UIThread.Post(UpdateTray);

    private void UpdateTray()
    {
        var view = UsagePresenter.Map(_monitor.Current);
        _tray.Icon = TrayIconRenderer.RenderIcon(view.Icon);
        _tray.ToolTipText = Tooltip(view);
        _panel?.Update(view);
    }

    private static string Tooltip(PanelView view)
    {
        if (view.IsDegraded)
            return $"{view.Provider.DisplayName}: нет данных";
        if (!view.Icon.IsDegraded && view.Limits.Count > 0)
            return $"{view.Provider.DisplayName}: {(int)Math.Round(view.Icon.WorstHeadroom)}% свободно";
        return view.Provider.DisplayName;
    }

    private void TogglePanel()
    {
        if (_panel is { IsVisible: true })
        {
            _panel.Hide();
            return;
        }

        // Force a fresh snapshot when opening (spec #5), then show what we have immediately.
        _ = _monitor.RefreshOnOpenAsync(CancellationToken.None);

        _panel ??= CreatePanel();
        _panel.Update(UsagePresenter.Map(_monitor.Current));
        _panel.Show();
        _panel.Activate();
    }

    private PanelWindow CreatePanel()
    {
        var panel = new PanelWindow();
        panel.Closed += (_, _) => _panel = null;
        return panel;
    }

    private static void Shutdown()
    {
        if (Application.Current?.ApplicationLifetime is IClassicDesktopStyleApplicationLifetime desktop)
            desktop.Shutdown();
    }

    public void Dispose()
    {
        _monitor.Updated -= OnUpdated;
        _tray.IsVisible = false;
        _tray.Dispose();
    }
}
