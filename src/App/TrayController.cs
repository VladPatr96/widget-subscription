using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Threading;
using WidgetSubscription.Core;

namespace WidgetSubscription.App;

/// <summary>
/// Owns the tray presence and the floating desktop <see cref="PanelWindow"/>, and marshals engine
/// updates onto the UI thread. Redraws the donut icon/tooltip and the widget on every
/// <see cref="UsageMonitor.Updated"/>. The tray icon has no native menu (its popup positioning is
/// unreliable on Windows) — left-click toggles the widget, and all commands live on the widget's
/// own correctly-positioned right-click menu. Exit is driven by the widget's context menu.
/// </summary>
public sealed class TrayController : IDisposable
{
    private readonly UsageMonitor _monitor;
    private readonly TrayIcon _tray;
    private readonly PanelWindow _widget;

    public TrayController(UsageMonitor monitor)
    {
        _monitor = monitor;

        _tray = new TrayIcon
        {
            ToolTipText = "Widget Subscription",
            IsVisible = true,
        };
        _tray.Clicked += (_, _) => ToggleWidget();

        _widget = new PanelWindow();
        _widget.ExitRequested += (_, _) => Shutdown();
        _widget.RefreshRequested += (_, _) => _ = _monitor.RefreshOnOpenAsync(CancellationToken.None);

        _monitor.Updated += OnUpdated;
        UpdateAll();

        // Land on the desktop as a collapsed icon from launch, so it is a movable widget, not a
        // tray-only popup.
        _widget.Show();
    }

    private void OnUpdated(object? sender, EventArgs e) => Dispatcher.UIThread.Post(UpdateAll);

    private void UpdateAll()
    {
        var view = UsagePresenter.Map(_monitor.Current);
        _tray.Icon = TrayIconRenderer.RenderIcon(view.Icon);
        _tray.ToolTipText = view.TooltipText;
        _widget.Update(view);
    }

    private void ToggleWidget()
    {
        if (_widget.IsVisible)
        {
            _widget.Hide();
            return;
        }

        _widget.Show();
        _widget.Activate();
        _ = _monitor.RefreshOnOpenAsync(CancellationToken.None);
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
        _widget.Close();
    }
}
