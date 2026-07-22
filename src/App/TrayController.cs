using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Threading;
using WidgetSubscription.Core;
using WidgetSubscription.Providers.ClaudeCode;

namespace WidgetSubscription.App;

/// <summary>
/// Owns the tray presence and the floating desktop <see cref="PanelWindow"/>, marshals engine
/// updates onto the UI thread, and drives the auth affordances (#18): the empty "sign in" state,
/// the source toggle, sign-out, and the interactive login (with its <see cref="LoginWaitingWindow"/>).
/// The two-mode credential logic lives in <see cref="SelectingCredentialSource"/>; this class only
/// reads the persisted <see cref="CredentialMode"/> and own-token presence to shape the UI.
/// </summary>
public sealed class TrayController : IDisposable
{
    private readonly UsageMonitor _monitor;
    private readonly ICredentialModeStore _modeStore;
    private readonly IWidgetTokenStore _tokenStore;
    private readonly Func<ICodeEntry, IWidgetLogin> _loginFactory;
    private readonly TrayIcon _tray;
    private readonly PanelWindow _widget;

    private LoginWaitingWindow? _loginWindow;
    private string? _loginNotice;

    public TrayController(
        UsageMonitor monitor,
        ICredentialModeStore modeStore,
        IWidgetTokenStore tokenStore,
        Func<ICodeEntry, IWidgetLogin> loginFactory)
    {
        _monitor = monitor;
        _modeStore = modeStore;
        _tokenStore = tokenStore;
        _loginFactory = loginFactory;

        _tray = new TrayIcon
        {
            ToolTipText = "Widget Subscription",
            IsVisible = true,
        };
        _tray.Clicked += (_, _) => ToggleWidget();

        _widget = new PanelWindow();
        _widget.ExitRequested += (_, _) => Shutdown();
        _widget.RefreshRequested += (_, _) => _ = _monitor.RefreshOnOpenAsync(CancellationToken.None);
        _widget.LoginRequested += (_, _) => StartLogin();
        _widget.SignOutRequested += (_, _) => SignOut();
        _widget.SourceToggleRequested += (_, _) => ToggleSource();

        _monitor.Updated += OnUpdated;
        UpdateAll();

        _widget.Show();
    }

    private void OnUpdated(object? sender, EventArgs e) => Dispatcher.UIThread.Post(UpdateAll);

    private void UpdateAll()
    {
        var state = _monitor.Current;
        if (state.Error is null)
            _loginNotice = null; // a good fetch clears any stale login-failure notice

        var view = UsagePresenter.Map(state);
        _tray.Icon = TrayIconRenderer.RenderIcon(view.Icon);
        _tray.ToolTipText = view.TooltipText;
        _widget.Update(view, ComputeAuth(state));
    }

    private AuthView ComputeAuth(UsageState state)
    {
        var mode = _modeStore.Get();
        var hasOwnGrant = _tokenStore.Load() is not null;
        var noCredentials = state.Error?.Kind == FetchErrorKind.NoCredentials;

        // Login is offered when nothing usable is available and we are not pinned to Claude Code.
        var loginRequired = noCredentials && mode != CredentialMode.Borrow && !hasOwnGrant;

        string? notice = _loginNotice;
        if (notice is null && noCredentials)
        {
            notice = mode switch
            {
                CredentialMode.Borrow => "Claude Code не найден. Переключите источник или откройте Claude Code.",
                // Own grant present but unusable ⇒ a transient refresh problem (§4.5), not a missing login.
                _ when hasOwnGrant => "Не удалось обновить сессию. Повторная попытка…",
                _ => null,
            };
        }

        return new AuthView(loginRequired, CanSignOut: hasOwnGrant, SourceIsOwn: mode == CredentialMode.Own, notice);
    }

    private async void StartLogin()
    {
        if (_loginWindow is not null)
        {
            _loginWindow.Activate();
            return;
        }

        var window = new LoginWaitingWindow();
        _loginWindow = window;
        window.Show();
        try
        {
            var login = _loginFactory(window);
            var result = await login.LoginAsync(window.Token);
            _loginNotice = result is LoginResult.Failed failed ? failed.Message : null;
            if (result is LoginResult.Success)
                _modeStore.Set(CredentialMode.Own);
        }
        finally
        {
            _loginWindow = null;
            window.Close();
            UpdateAll();
            _ = _monitor.RefreshOnOpenAsync(CancellationToken.None);
        }
    }

    private void SignOut()
    {
        // Sign-out (#18 §3.5) discards the own grant; the mode flag (#17 override) is left untouched.
        _tokenStore.Clear();
        _loginNotice = null;
        UpdateAll();
        _ = _monitor.RefreshOnOpenAsync(CancellationToken.None);
    }

    private void ToggleSource()
    {
        // Binary toggle: own ⇄ Claude Code (Auto). Auto counts as the Claude Code side.
        var next = _modeStore.Get() == CredentialMode.Own ? CredentialMode.Auto : CredentialMode.Own;
        _modeStore.Set(next);
        _loginNotice = null;
        UpdateAll();
        _ = _monitor.RefreshOnOpenAsync(CancellationToken.None);
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
        _loginWindow?.Close();
        _widget.Close();
    }
}
