using Avalonia;
using Avalonia.Controls;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Threading;
using WidgetSubscription.Providers.ClaudeCode;

namespace WidgetSubscription.App;

/// <summary>
/// The modal-style wait window shown during an own-login attempt (#18 §3.3). It stays open across
/// focus loss (the tray popover closes when focus goes to the browser, so login status lives here,
/// not in the panel): a spinner-less "waiting" message with a Cancel button, and — when the flow
/// falls back to hosted-paste — a code entry box. It doubles as the <see cref="ICodeEntry"/> the
/// login orchestrator drives, and owns the login <see cref="CancellationToken"/> (Cancel cancels it).
/// </summary>
public sealed class LoginWaitingWindow : Window, ICodeEntry, IManualEntrySignal
{
    private static readonly IBrush Ink = Brushes.White;
    private static readonly IBrush Surface = SolidColorBrush.Parse("#161b22");

    private readonly CancellationTokenSource _cts = new();
    private readonly CancellationTokenSource _manual = new();
    private readonly StackPanel _waitingPanel;
    private readonly StackPanel _pastePanel;
    private readonly TextBox _codeBox;
    private TaskCompletionSource<string?>? _pending;

    /// <summary>Cancelled when the user cancels or closes the window; drives the login flow.</summary>
    public CancellationToken Token => _cts.Token;

    /// <summary>Cancelled when the user clicks "Ввести код вручную"; abandons the loopback wait (#18 §3.3).</summary>
    public CancellationToken ManualEntryRequested => _manual.Token;

    public LoginWaitingWindow()
    {
        Title = "Вход через Anthropic";
        Width = 360;
        SizeToContent = SizeToContent.Height;
        CanResize = false;
        Topmost = true;
        WindowStartupLocation = WindowStartupLocation.CenterScreen;
        Background = Surface;
        Icon = new WindowIcon(new MemoryStream(AppIconRenderer.RenderPng(64)));

        var status = new TextBlock
        {
            Text = "Ожидаем подтверждения в браузере…\nЗавершите вход на открывшейся странице.",
            Foreground = Ink,
            TextWrapping = TextWrapping.Wrap,
        };
        var cancelWaiting = new Button { Content = "Отмена" };
        cancelWaiting.Click += (_, _) => Cancel();
        var manualEntry = new Button { Content = "Ввести код вручную", Background = Brushes.Transparent, BorderThickness = new Thickness(0) };
        manualEntry.Click += (_, _) => { if (!_manual.IsCancellationRequested) _manual.Cancel(); };
        var waitingButtons = new StackPanel
        {
            Orientation = Orientation.Horizontal,
            Spacing = 8,
            HorizontalAlignment = HorizontalAlignment.Right,
            Children = { manualEntry, cancelWaiting },
        };
        _waitingPanel = new StackPanel { Spacing = 14, Children = { status, waitingButtons } };

        _codeBox = new TextBox { Watermark = "код из браузера" };
        var submit = new Button { Content = "Подтвердить" };
        submit.Click += (_, _) => _pending?.TrySetResult(_codeBox.Text);
        var cancelPaste = new Button { Content = "Отмена" };
        cancelPaste.Click += (_, _) => Cancel();
        var pasteButtons = new StackPanel
        {
            Orientation = Orientation.Horizontal,
            Spacing = 8,
            HorizontalAlignment = HorizontalAlignment.Right,
            Children = { submit, cancelPaste },
        };
        _pastePanel = new StackPanel
        {
            Spacing = 14,
            IsVisible = false,
            Children =
            {
                new TextBlock { Text = "Вставьте код, показанный в браузере:", Foreground = Ink, TextWrapping = TextWrapping.Wrap },
                _codeBox,
                pasteButtons,
            },
        };

        Content = new Border
        {
            Padding = new Thickness(18),
            Child = new StackPanel { Children = { _waitingPanel, _pastePanel } },
        };
    }

    /// <summary><see cref="ICodeEntry"/>: reveal the paste box and await the user's code (or cancel).</summary>
    public Task<string?> PromptAsync(CancellationToken ct)
        => Dispatcher.UIThread.InvokeAsync(() =>
        {
            _waitingPanel.IsVisible = false;
            _pastePanel.IsVisible = true;
            _codeBox.Focus();
            _pending = new TaskCompletionSource<string?>(TaskCreationOptions.RunContinuationsAsynchronously);
            ct.Register(() => _pending?.TrySetResult(null));
            return _pending.Task;
        });

    private void Cancel()
    {
        _pending?.TrySetResult(null);
        if (!_cts.IsCancellationRequested)
            _cts.Cancel();
        Close();
    }

    protected override void OnClosed(EventArgs e)
    {
        // A window closed by any means (X button, owner) must not leave the flow hanging.
        _pending?.TrySetResult(null);
        if (!_cts.IsCancellationRequested)
            _cts.Cancel();
        _cts.Dispose();
        _manual.Dispose();
        base.OnClosed(e);
    }
}
