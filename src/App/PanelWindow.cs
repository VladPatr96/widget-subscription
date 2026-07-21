using Avalonia;
using Avalonia.Controls;
using Avalonia.Layout;
using Avalonia.Media;
using WidgetSubscription.Core;

namespace WidgetSubscription.App;

/// <summary>
/// The click-to-open panel (spec #3): three bars (headroom %, badge, reset countdown), the
/// provider name, and a footer that shows the degradation reason or the stale-data age. Built in
/// code so it can redraw straight from a <see cref="PanelView"/>; all thresholds/colors/texts
/// come pre-computed from <see cref="UsagePresenter"/>.
/// </summary>
public sealed class PanelWindow : Window
{
    private static readonly IBrush Ink = Brushes.White;
    private static readonly IBrush Muted = SolidColorBrush.Parse("#8b949e");
    private static readonly IBrush TrackBrush = SolidColorBrush.Parse("#21262d");

    private readonly TextBlock _header;
    private readonly StackPanel _rows;
    private readonly TextBlock _footer;

    public PanelWindow()
    {
        Width = 300;
        SizeToContent = SizeToContent.Height;
        SystemDecorations = SystemDecorations.BorderOnly;
        ShowInTaskbar = false;
        CanResize = false;
        Topmost = true;
        WindowStartupLocation = WindowStartupLocation.Manual;
        Title = "Widget Subscription";
        Background = SolidColorBrush.Parse("#161b22");

        _header = new TextBlock { FontSize = 13, FontWeight = FontWeight.SemiBold, Foreground = Ink };
        _rows = new StackPanel { Spacing = 14 };
        _footer = new TextBlock { FontSize = 12, Foreground = Muted, TextWrapping = TextWrapping.Wrap };

        Content = new StackPanel
        {
            Margin = new Thickness(14),
            Spacing = 12,
            Children = { _header, _rows, _footer },
        };

        // SizeToContent.Height resolves the final height on a later layout pass; re-pin then so the
        // bottom edge stays anchored (Bounds is stale at OnOpened/Update time).
        SizeChanged += (_, _) => RepositionIfVisible();
    }

    /// <summary>
    /// Anchor the panel to the primary screen's notification-area corner (bottom-right of the
    /// working area, i.e. above the taskbar). Avalonia's <see cref="TrayIcon"/> exposes no screen
    /// coordinates, so this is the closest reliable anchor to the actual tray icon.
    /// </summary>
    private void PositionBottomRight()
    {
        var screen = Screens.Primary;
        if (screen is null)
            return;

        var area = screen.WorkingArea;
        var scale = screen.Scaling;
        var margin = (int)Math.Round(8 * scale);
        var width = (int)Math.Ceiling(Bounds.Width * scale);
        var height = (int)Math.Ceiling(Bounds.Height * scale);

        Position = new PixelPoint(
            area.X + area.Width - width - margin,
            area.Y + area.Height - height - margin);
    }

    private void RepositionIfVisible()
    {
        if (IsVisible)
            PositionBottomRight();
    }

    protected override void OnOpened(EventArgs e)
    {
        base.OnOpened(e);
        PositionBottomRight();
    }

    public void Update(PanelView view)
    {
        _header.Text = view.Provider.DisplayName;

        _rows.Children.Clear();
        foreach (var limit in view.Limits)
            _rows.Children.Add(BuildRow(limit));

        _footer.Text = view switch
        {
            { IsDegraded: true } => view.DegradedReason ?? "Нет данных",
            { IsStale: true, AgeText: { } age } => age,
            _ => string.Empty,
        };
        _footer.IsVisible = !string.IsNullOrEmpty(_footer.Text);

        RepositionIfVisible();
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
