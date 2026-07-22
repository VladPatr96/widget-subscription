using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Media.Imaging;
using Avalonia.VisualTree;
using WidgetSubscription.Core;

namespace WidgetSubscription.App;

/// <summary>
/// The floating desktop widget: a frameless, always-on-top window the user drags anywhere within
/// the screen's working area. It has two states — collapsed to a donut icon, or expanded to the
/// full three-bar panel (spec #3) — toggled by clicking the collapsed icon or the header button.
/// Right-click opens an Avalonia <see cref="ContextMenu"/> (correctly positioned at the cursor,
/// unlike the native tray popup) with collapse/expand and exit. All thresholds/colors/texts come
/// pre-computed from <see cref="UsagePresenter"/>; this class only lays them out and owns window
/// behaviour (drag, clamp-to-screen, state toggle).
/// </summary>
public sealed class PanelWindow : Window
{
    private const int CollapsedSize = 56;
    private const int ExpandedWidth = 300;
    private const double DragThreshold = 4;

    private static readonly IBrush Ink = Brushes.White;
    private static readonly IBrush Muted = SolidColorBrush.Parse("#8b949e");
    private static readonly IBrush TrackBrush = SolidColorBrush.Parse("#21262d");
    private static readonly IBrush Surface = SolidColorBrush.Parse("#161b22");
    private static readonly IBrush Edge = SolidColorBrush.Parse("#30363d");

    private readonly Image _iconImage;
    private readonly Border _collapsedView;
    private readonly Border _expandedView;
    private readonly TextBlock _header;
    private readonly StackPanel _rows;
    private readonly TextBlock _footer;
    private readonly StackPanel _loginPanel;

    private Bitmap? _iconBitmap;
    private bool _collapsed = true;
    private bool _positioned;
    private AuthView _lastAuth = new(false, false, false, null);

    // Manual drag state — lets us distinguish a click (expand) from a drag (move) on the same
    // surface, which BeginMoveDrag cannot do because it swallows the click.
    private bool _pointerDown;
    private bool _dragging;
    private PixelPoint _pressPointer;
    private PixelPoint _pressWindow;

    /// <summary>Raised when the user picks "Выход" from the context menu.</summary>
    public event EventHandler? ExitRequested;

    /// <summary>Raised when the widget expands, so the owner can force a fresh fetch (spec #5).</summary>
    public event EventHandler? RefreshRequested;

    /// <summary>Raised when the user clicks "Войти через Anthropic" (empty state or menu).</summary>
    public event EventHandler? LoginRequested;

    /// <summary>Raised when the user picks "Выйти из аккаунта" (own-login sign-out).</summary>
    public event EventHandler? SignOutRequested;

    /// <summary>Raised when the user toggles the credential source in the menu.</summary>
    public event EventHandler? SourceToggleRequested;

    public PanelWindow()
    {
        SystemDecorations = SystemDecorations.None;
        ShowInTaskbar = false;
        CanResize = false;
        Topmost = true;
        WindowStartupLocation = WindowStartupLocation.Manual;
        Background = Brushes.Transparent;
        Title = "Widget Subscription";

        _iconImage = new Image
        {
            Width = 40,
            Height = 40,
            Stretch = Stretch.Uniform,
            HorizontalAlignment = HorizontalAlignment.Center,
            VerticalAlignment = VerticalAlignment.Center,
        };
        _collapsedView = new Border
        {
            Width = CollapsedSize,
            Height = CollapsedSize,
            CornerRadius = new CornerRadius(12),
            Background = Surface,
            BorderBrush = Edge,
            BorderThickness = new Thickness(1),
            Child = _iconImage,
        };

        _header = new TextBlock
        {
            FontSize = 13,
            FontWeight = FontWeight.SemiBold,
            Foreground = Ink,
            VerticalAlignment = VerticalAlignment.Center,
        };
        var collapseButton = new Button
        {
            Content = "\u2013",
            Width = 24,
            Height = 24,
            Padding = new Thickness(0),
            FontSize = 16,
            Background = Brushes.Transparent,
            Foreground = Muted,
            BorderThickness = new Thickness(0),
            HorizontalContentAlignment = HorizontalAlignment.Center,
            VerticalContentAlignment = VerticalAlignment.Center,
        };
        collapseButton.Click += (_, _) => SetCollapsed(true);
        var headerRow = new Grid { ColumnDefinitions = new ColumnDefinitions("*,Auto") };
        headerRow.Children.Add(_header);
        Grid.SetColumn(collapseButton, 1);
        headerRow.Children.Add(collapseButton);

        _rows = new StackPanel { Spacing = 14 };
        _footer = new TextBlock { FontSize = 12, Foreground = Muted, TextWrapping = TextWrapping.Wrap };

        var loginButton = new Button
        {
            Content = "Войти через Anthropic",
            HorizontalAlignment = HorizontalAlignment.Stretch,
            HorizontalContentAlignment = HorizontalAlignment.Center,
            Background = SolidColorBrush.Parse("#D97757"),
            Foreground = Brushes.White,
            BorderThickness = new Thickness(0),
            CornerRadius = new CornerRadius(8),
            Padding = new Thickness(0, 8, 0, 8),
        };
        loginButton.Click += (_, _) => LoginRequested?.Invoke(this, EventArgs.Empty);
        _loginPanel = new StackPanel
        {
            IsVisible = false,
            Spacing = 10,
            Children =
            {
                new TextBlock
                {
                    Text = "Войдите через Anthropic, чтобы видеть лимиты подписки.",
                    Foreground = Muted,
                    FontSize = 13,
                    TextWrapping = TextWrapping.Wrap,
                },
                loginButton,
            },
        };

        _expandedView = new Border
        {
            CornerRadius = new CornerRadius(12),
            Background = Surface,
            BorderBrush = Edge,
            BorderThickness = new Thickness(1),
            Child = new StackPanel
            {
                Margin = new Thickness(14),
                Spacing = 12,
                Children = { headerRow, _rows, _loginPanel, _footer },
            },
        };

        Content = new Panel { Children = { _collapsedView, _expandedView } };
        ContextMenu = BuildContextMenu(new AuthView(false, false, false, null));
        ApplyState();

        // SizeToContent resolves the final height on a later layout pass; keep the widget on-screen
        // whenever its size changes (collapse/expand or content growth).
        SizeChanged += (_, _) => ClampToScreen();
    }

    private ContextMenu BuildContextMenu(AuthView auth)
    {
        var menu = new ContextMenu();

        var toggle = new MenuItem { Header = "Развернуть / свернуть" };
        toggle.Click += (_, _) => SetCollapsed(!_collapsed);
        menu.Items.Add(toggle);

        var source = new MenuItem
        {
            Header = auth.SourceIsOwn ? "Источник: собственный вход" : "Источник: Claude Code",
        };
        source.Click += (_, _) => SourceToggleRequested?.Invoke(this, EventArgs.Empty);
        menu.Items.Add(source);

        if (auth.LoginRequired)
        {
            var login = new MenuItem { Header = "Войти через Anthropic" };
            login.Click += (_, _) => LoginRequested?.Invoke(this, EventArgs.Empty);
            menu.Items.Add(login);
        }

        if (auth.CanSignOut)
        {
            var signOut = new MenuItem { Header = "Выйти из аккаунта" };
            signOut.Click += (_, _) => SignOutRequested?.Invoke(this, EventArgs.Empty);
            menu.Items.Add(signOut);
        }

        var exit = new MenuItem { Header = "Выход" };
        exit.Click += (_, _) => ExitRequested?.Invoke(this, EventArgs.Empty);
        menu.Items.Add(exit);

        return menu;
    }

    public void Update(PanelView view, AuthView auth)
    {
        _iconImage.Source = RenderIcon(view.Icon);
        _header.Text = view.Provider.DisplayName;

        _loginPanel.IsVisible = auth.LoginRequired;
        _rows.IsVisible = !auth.LoginRequired;

        _rows.Children.Clear();
        if (!auth.LoginRequired)
            foreach (var limit in view.Limits)
                _rows.Children.Add(BuildRow(limit));

        var footer = auth.Notice ?? view switch
        {
            { IsDegraded: true } => view.DegradedReason ?? "Нет данных",
            { IsStale: true, AgeText: { } age } => age,
            _ => string.Empty,
        };
        _footer.Text = footer;
        _footer.IsVisible = !string.IsNullOrEmpty(footer);

        if (!auth.Equals(_lastAuth))
        {
            _lastAuth = auth;
            ContextMenu = BuildContextMenu(auth);
        }
    }

    /// <summary>Shows the widget collapsed and force-refreshes; called by the owner on expand.</summary>
    public void RequestRefresh() => RefreshRequested?.Invoke(this, EventArgs.Empty);

    private Bitmap RenderIcon(IconView icon)
    {
        var png = DonutRenderer.RenderPng(icon, 96);
        using var stream = new MemoryStream(png);
        var bitmap = new Bitmap(stream);
        _iconBitmap?.Dispose();
        _iconBitmap = bitmap;
        return bitmap;
    }

    private void SetCollapsed(bool collapsed)
    {
        if (_collapsed == collapsed)
            return;
        _collapsed = collapsed;
        ApplyState();
        if (!collapsed)
            RefreshRequested?.Invoke(this, EventArgs.Empty);
    }

    private void ApplyState()
    {
        _collapsedView.IsVisible = _collapsed;
        _expandedView.IsVisible = !_collapsed;
        if (_collapsed)
        {
            SizeToContent = SizeToContent.Manual;
            Width = CollapsedSize;
            Height = CollapsedSize;
        }
        else
        {
            Width = ExpandedWidth;
            SizeToContent = SizeToContent.Height;
        }
    }

    protected override void OnOpened(EventArgs e)
    {
        base.OnOpened(e);
        if (_positioned)
            return;
        PositionBottomRight();
        _positioned = true;
    }

    /// <summary>Default landing spot: the working area's bottom-right corner (near the tray).</summary>
    private void PositionBottomRight()
    {
        var screen = Screens.ScreenFromVisual(this) ?? Screens.Primary;
        if (screen is null)
            return;

        var area = screen.WorkingArea;
        var margin = (int)Math.Round(16 * screen.Scaling);
        var width = (int)Math.Ceiling(Bounds.Width * screen.Scaling);
        var height = (int)Math.Ceiling(Bounds.Height * screen.Scaling);

        Position = new PixelPoint(
            area.X + area.Width - width - margin,
            area.Y + area.Height - height - margin);
    }

    protected override void OnPointerPressed(PointerPressedEventArgs e)
    {
        base.OnPointerPressed(e);
        var point = e.GetCurrentPoint(this);
        if (!point.Properties.IsLeftButtonPressed)
            return;
        // Let the collapse button (or any control) handle its own click instead of starting a drag.
        if (e.Source is Visual source && source.FindAncestorOfType<Button>(includeSelf: true) is not null)
            return;

        _pointerDown = true;
        _dragging = false;
        _pressPointer = this.PointToScreen(point.Position);
        _pressWindow = Position;
        e.Pointer.Capture(this);
    }

    protected override void OnPointerMoved(PointerEventArgs e)
    {
        base.OnPointerMoved(e);
        if (!_pointerDown)
            return;

        var current = this.PointToScreen(e.GetPosition(this));
        var dx = current.X - _pressPointer.X;
        var dy = current.Y - _pressPointer.Y;
        if (!_dragging && Math.Abs(dx) + Math.Abs(dy) < DragThreshold)
            return;

        _dragging = true;
        Position = ClampPosition(new PixelPoint(_pressWindow.X + dx, _pressWindow.Y + dy));
    }

    protected override void OnPointerReleased(PointerReleasedEventArgs e)
    {
        base.OnPointerReleased(e);
        if (!_pointerDown)
            return;

        _pointerDown = false;
        e.Pointer.Capture(null);

        // A press with no meaningful movement is a click: expand the collapsed icon.
        if (!_dragging && e.InitialPressMouseButton == MouseButton.Left && _collapsed)
            SetCollapsed(false);
        _dragging = false;
    }

    private void ClampToScreen()
    {
        if (_positioned)
            Position = ClampPosition(Position);
    }

    private PixelPoint ClampPosition(PixelPoint desired)
    {
        var screen = Screens.ScreenFromVisual(this) ?? Screens.Primary;
        if (screen is null)
            return desired;

        var area = screen.WorkingArea;
        var width = (int)Math.Ceiling(Bounds.Width * screen.Scaling);
        var height = (int)Math.Ceiling(Bounds.Height * screen.Scaling);
        var maxX = Math.Max(area.X, area.X + area.Width - width);
        var maxY = Math.Max(area.Y, area.Y + area.Height - height);

        return new PixelPoint(
            Math.Clamp(desired.X, area.X, maxX),
            Math.Clamp(desired.Y, area.Y, maxY));
    }

    private static Control BuildRow(LimitView limit)
    {
        var name = new TextBlock { Text = limit.DisplayName, Foreground = Ink, FontSize = 13 };
        var headroom = new TextBlock
        {
            Text = $"{(int)Math.Round(limit.Headroom)}%",
            Foreground = Ink,
            FontSize = 13,
            FontWeight = FontWeight.Bold,
            HorizontalAlignment = HorizontalAlignment.Right,
        };
        var top = new Grid { ColumnDefinitions = new ColumnDefinitions("*,Auto") };
        Grid.SetColumn(headroom, 1);
        top.Children.Add(name);
        top.Children.Add(headroom);

        var filled = Math.Clamp((int)Math.Round(limit.Headroom), 0, 100);
        var barGrid = new Grid { ColumnDefinitions = new ColumnDefinitions($"{filled}*,{100 - filled}*") };
        var fill = new Border
        {
            CornerRadius = new CornerRadius(5),
            Background = SolidColorBrush.Parse(limit.StatusColor),
        };
        barGrid.Children.Add(fill);
        var bar = new Border
        {
            Height = 8,
            CornerRadius = new CornerRadius(5),
            Background = TrackBrush,
            ClipToBounds = true,
            Child = barGrid,
        };

        var statusColor = Color.Parse(limit.StatusColor);
        var badge = new Border
        {
            CornerRadius = new CornerRadius(20),
            Padding = new Thickness(7, 1, 7, 1),
            Background = new SolidColorBrush(new Color(0x26, statusColor.R, statusColor.G, statusColor.B)),
            HorizontalAlignment = HorizontalAlignment.Left,
            Child = new TextBlock
            {
                Text = limit.StatusLabel,
                FontSize = 11,
                FontWeight = FontWeight.SemiBold,
                Foreground = new SolidColorBrush(statusColor),
            },
        };
        var reset = new TextBlock
        {
            Text = limit.ResetText,
            FontSize = 12,
            Foreground = Muted,
            HorizontalAlignment = HorizontalAlignment.Right,
            VerticalAlignment = VerticalAlignment.Center,
        };
        ToolTip.SetTip(reset, limit.ResetsAt.ToLocalTime().ToString("f"));
        var meta = new Grid { ColumnDefinitions = new ColumnDefinitions("Auto,*") };
        Grid.SetColumn(reset, 1);
        meta.Children.Add(badge);
        meta.Children.Add(reset);

        return new StackPanel { Spacing = 5, Children = { top, bar, meta } };
    }
}
