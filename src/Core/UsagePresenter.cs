namespace WidgetSubscription.Core;

/// <summary>Threshold state of a single limit (spec #3): ок &gt;30 · близко 10–30 · критично &lt;10 · исчерпан 0.</summary>
public enum LimitStatus { Ok, Close, Critical, Exhausted }

/// <summary>Everything the tray icon needs, already reduced from the worst of the three limits.</summary>
/// <param name="WorstHeadroom">The smallest headroom across the limits (0..100).</param>
/// <param name="Status">Threshold state of the worst headroom.</param>
/// <param name="Color">Hex color to paint the donut arc — grey when degraded.</param>
/// <param name="ArcFraction">Filled fraction of the donut, 0..1 = worst headroom / 100.</param>
/// <param name="IsDegraded">Whether the latest fetch failed (grey donut).</param>
public sealed record IconView(
    double WorstHeadroom,
    LimitStatus Status,
    string Color,
    double ArcFraction,
    bool IsDegraded);

/// <summary>One panel row for a limit.</summary>
public sealed record LimitView(
    LimitKind Kind,
    string DisplayName,
    double Headroom,
    LimitStatus Status,
    string StatusColor,
    string StatusLabel,
    string ResetText,
    DateTimeOffset ResetsAt);

/// <summary>The whole panel view (spec #3): icon + three rows + provider identity + degradation/age.</summary>
public sealed record PanelView(
    ProviderInfo Provider,
    IconView Icon,
    IReadOnlyList<LimitView> Limits,
    bool IsDegraded,
    string? DegradedReason,
    bool IsStale,
    TimeSpan? Age,
    string? AgeText);

/// <summary>
/// Pure mapping from <see cref="UsageState"/> to the view model the UI paints (spec #3). No UI,
/// no I/O, no clocks of its own — it reads <see cref="UsageState.Now"/> so countdowns are
/// deterministic. Thresholds, colors, worst-of-three, and reset/age text all live here so the
/// Avalonia layer stays dumb.
/// </summary>
public static class UsagePresenter
{
    private const string OkColor = "#2ea043";
    private const string CloseColor = "#d29922";
    private const string CriticalColor = "#e5484d";
    private const string ExhaustedColor = "#8b1a1f";
    private const string DegradedColor = "#8b949e";

    public static PanelView Map(UsageState state)
    {
        var limits = state.Snapshot is { } snapshot
            ? snapshot.Limits.Select(limit => MapLimit(limit, state.Now)).ToArray()
            : Array.Empty<LimitView>();

        var worst = limits.Length > 0 ? limits.Min(l => l.Headroom) : 0d;
        var worstStatus = StatusOf(worst);

        var icon = new IconView(
            WorstHeadroom: worst,
            Status: worstStatus,
            Color: state.IsDegraded ? DegradedColor : ColorOf(worstStatus),
            ArcFraction: Math.Clamp(worst / 100d, 0d, 1d),
            IsDegraded: state.IsDegraded);

        var ageText = state.IsStale && state.Age is { } age ? FormatAge(age) : null;

        return new PanelView(
            Provider: state.Provider,
            Icon: icon,
            Limits: limits,
            IsDegraded: state.IsDegraded,
            DegradedReason: state.IsDegraded ? state.Error?.Message : null,
            IsStale: state.IsStale,
            Age: state.Age,
            AgeText: ageText);
    }

    private static LimitView MapLimit(Limit limit, DateTimeOffset now)
    {
        var status = StatusOf(limit.Headroom);
        return new LimitView(
            Kind: limit.Kind,
            DisplayName: limit.DisplayName,
            Headroom: limit.Headroom,
            Status: status,
            StatusColor: ColorOf(status),
            StatusLabel: LabelOf(status),
            ResetText: FormatReset(limit.ResetsAt - now, status == LimitStatus.Exhausted),
            ResetsAt: limit.ResetsAt);
    }

    // Matches the prototype: hr<=0 exhausted, <10 critical, <=30 close, else ok.
    public static LimitStatus StatusOf(double headroom) => headroom switch
    {
        <= 0 => LimitStatus.Exhausted,
        < 10 => LimitStatus.Critical,
        <= 30 => LimitStatus.Close,
        _ => LimitStatus.Ok,
    };

    private static string ColorOf(LimitStatus status) => status switch
    {
        LimitStatus.Ok => OkColor,
        LimitStatus.Close => CloseColor,
        LimitStatus.Critical => CriticalColor,
        LimitStatus.Exhausted => ExhaustedColor,
        _ => DegradedColor,
    };

    private static string LabelOf(LimitStatus status) => status switch
    {
        LimitStatus.Ok => "ок",
        LimitStatus.Close => "близко",
        LimitStatus.Critical => "критично",
        LimitStatus.Exhausted => "исчерпан",
        _ => "нет данных",
    };

    private static string FormatReset(TimeSpan delta, bool exhausted)
    {
        if (delta <= TimeSpan.Zero)
            return "сброшено";

        string core;
        if (delta.TotalDays >= 1)
            core = $"через {(int)delta.TotalDays} дн";
        else if (delta.TotalHours >= 1)
        {
            var hours = (int)delta.TotalHours;
            var minutes = delta.Minutes;
            core = minutes > 0 ? $"через {hours} ч {minutes} мин" : $"через {hours} ч";
        }
        else
            core = $"через {Math.Max(1, (int)delta.TotalMinutes)} мин";

        return exhausted ? "сброс " + core : core;
    }

    private static string FormatAge(TimeSpan age)
    {
        if (age.TotalMinutes < 1)
            return $"данные {(int)age.TotalSeconds} с назад";
        if (age.TotalHours < 1)
            return $"данные {(int)age.TotalMinutes} мин назад";
        return $"данные {(int)age.TotalHours} ч назад";
    }
}
