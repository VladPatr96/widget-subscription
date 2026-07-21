using WidgetSubscription.Core;
using Xunit;

namespace WidgetSubscription.Tests;

/// <summary>
/// The presentation mapping (spec #3): thresholds, colors, worst-of-three for the icon, reset
/// countdown text, degradation to a grey donut with a reason, and stale age text. Pure and
/// deterministic — every case pins <see cref="UsageState.Now"/>.
/// </summary>
public class UsagePresenterTests
{
    private static readonly ProviderInfo Provider = new("claude-code", "Claude Code", "#D97757");
    private static readonly DateTimeOffset Now = new(2026, 7, 21, 12, 0, 0, TimeSpan.Zero);

    [Theory]
    [InlineData(100, LimitStatus.Ok)]
    [InlineData(31, LimitStatus.Ok)]
    [InlineData(30, LimitStatus.Close)]
    [InlineData(10, LimitStatus.Close)]
    [InlineData(9.9, LimitStatus.Critical)]
    [InlineData(0.1, LimitStatus.Critical)]
    [InlineData(0, LimitStatus.Exhausted)]
    public void Thresholds_follow_the_fixed_boundaries(double headroom, LimitStatus expected)
        => Assert.Equal(expected, UsagePresenter.StatusOf(headroom));

    [Fact]
    public void Icon_encodes_the_worst_of_the_three_limits()
    {
        var panel = UsagePresenter.Map(StateWith(
            Limit(LimitKind.Session, 12),
            Limit(LimitKind.WeeklyAll, 60),
            Limit(LimitKind.WeeklyScoped, 34)));

        Assert.Equal(12, panel.Icon.WorstHeadroom);
        Assert.Equal(LimitStatus.Close, panel.Icon.Status);
        Assert.Equal("#d29922", panel.Icon.Color);
        Assert.Equal(0.12, panel.Icon.ArcFraction, 3);
        Assert.False(panel.Icon.IsDegraded);
    }

    [Fact]
    public void Each_limit_gets_status_color_and_label()
    {
        var panel = UsagePresenter.Map(StateWith(
            Limit(LimitKind.Session, 0),
            Limit(LimitKind.WeeklyAll, 41),
            Limit(LimitKind.WeeklyScoped, 4)));

        var session = panel.Limits.Single(l => l.Kind == LimitKind.Session);
        Assert.Equal(LimitStatus.Exhausted, session.Status);
        Assert.Equal("#8b1a1f", session.StatusColor);
        Assert.Equal("исчерпан", session.StatusLabel);

        var weekly = panel.Limits.Single(l => l.Kind == LimitKind.WeeklyAll);
        Assert.Equal("ок", weekly.StatusLabel);

        var fable = panel.Limits.Single(l => l.Kind == LimitKind.WeeklyScoped);
        Assert.Equal(LimitStatus.Critical, fable.Status);
        Assert.Equal("критично", fable.StatusLabel);
    }

    [Fact]
    public void Reset_text_is_relative_and_exhausted_limits_say_sbros()
    {
        var panel = UsagePresenter.Map(StateWith(
            new Limit(LimitKind.Session, "5-hour", 0, Now.AddHours(2).AddMinutes(14), true),
            new Limit(LimitKind.WeeklyAll, "Weekly", 41, Now.AddDays(3), false),
            new Limit(LimitKind.WeeklyScoped, "Fable 5", 4, Now.AddMinutes(48), false)));

        Assert.Equal("сброс через 2 ч 14 мин", panel.Limits.Single(l => l.Kind == LimitKind.Session).ResetText);
        Assert.Equal("через 3 дн", panel.Limits.Single(l => l.Kind == LimitKind.WeeklyAll).ResetText);
        Assert.Equal("через 48 мин", panel.Limits.Single(l => l.Kind == LimitKind.WeeklyScoped).ResetText);
    }

    [Fact]
    public void Already_reset_limit_reads_sbrosheno()
    {
        var panel = UsagePresenter.Map(StateWith(
            new Limit(LimitKind.Session, "5-hour", 100, Now.AddMinutes(-5), false),
            Limit(LimitKind.WeeklyAll, 98),
            Limit(LimitKind.WeeklyScoped, 98)));

        Assert.Equal("сброшено", panel.Limits.Single(l => l.Kind == LimitKind.Session).ResetText);
    }

    [Fact]
    public void Reset_countdown_uses_absolute_reset_even_on_a_stale_snapshot()
    {
        var resetsAt = Now.AddHours(3);
        // Snapshot fetched 10 minutes ago; the panel is rendered "now".
        var state = new UsageState(
            Provider,
            new UsageSnapshot(new[] { new Limit(LimitKind.Session, "5-hour", 50, resetsAt, true) }, Now.AddMinutes(-10)),
            Error: null,
            FetchedAt: Now.AddMinutes(-10),
            Now: Now,
            StaleAfter: TimeSpan.FromSeconds(90));

        var panel = UsagePresenter.Map(state);

        Assert.True(panel.IsStale);
        // 3h from now, not 3h from the 10-min-old fetch.
        Assert.Equal("через 3 ч", panel.Limits[0].ResetText);
    }

    [Fact]
    public void Fresh_snapshot_with_a_transient_error_keeps_showing_the_cached_data()
    {
        var state = new UsageState(
            Provider,
            new UsageSnapshot(new[] { new Limit(LimitKind.Session, "5-hour", 80, Now.AddHours(1), true) }, Now),
            Error: new FetchError(FetchErrorKind.SourceUnavailable, "Claude Code вернул 429."),
            FetchedAt: Now,
            Now: Now,
            StaleAfter: TimeSpan.FromSeconds(90));

        var panel = UsagePresenter.Map(state);

        Assert.False(panel.IsDegraded);                     // transient error hidden while data is fresh
        Assert.False(panel.Icon.IsDegraded);
        Assert.Equal("#2ea043", panel.Icon.Color);          // normal threshold color, not grey
        Assert.Null(panel.DegradedReason);
        Assert.Single(panel.Limits);
    }

    [Fact]
    public void Stale_snapshot_with_an_error_greys_the_icon_and_shows_a_reason()
    {
        var state = new UsageState(
            Provider,
            new UsageSnapshot(new[] { new Limit(LimitKind.Session, "5-hour", 80, Now.AddHours(1), true) }, Now.AddMinutes(-2)),
            Error: new FetchError(FetchErrorKind.SourceUnavailable, "Источник Claude Code недоступен"),
            FetchedAt: Now.AddMinutes(-2),
            Now: Now,
            StaleAfter: TimeSpan.FromSeconds(90));

        var panel = UsagePresenter.Map(state);

        Assert.True(panel.IsDegraded);
        Assert.True(panel.Icon.IsDegraded);
        Assert.Equal("#8b949e", panel.Icon.Color);          // grey overrides the threshold color
        Assert.Equal("Источник Claude Code недоступен", panel.DegradedReason);
        Assert.Single(panel.Limits);                        // last-good rows still shown
    }

    [Fact]
    public void Total_failure_still_shows_provider_identity_and_no_rows()
    {
        var state = new UsageState(
            Provider,
            Snapshot: null,
            Error: new FetchError(FetchErrorKind.NoCredentials, "Нет учётных данных"),
            FetchedAt: null,
            Now: Now,
            StaleAfter: TimeSpan.FromSeconds(90));

        var panel = UsagePresenter.Map(state);

        Assert.Equal("Claude Code", panel.Provider.DisplayName);
        Assert.True(panel.Icon.IsDegraded);
        Assert.Empty(panel.Limits);
        Assert.Equal("Нет учётных данных", panel.DegradedReason);
        Assert.Null(panel.AgeText);
    }

    [Fact]
    public void Stale_snapshot_exposes_age_text()
    {
        var state = new UsageState(
            Provider,
            new UsageSnapshot(new[] { Limit(LimitKind.Session, 50) }, Now.AddMinutes(-2)),
            Error: null,
            FetchedAt: Now.AddMinutes(-2),
            Now: Now,
            StaleAfter: TimeSpan.FromSeconds(90));

        var panel = UsagePresenter.Map(state);

        Assert.True(panel.IsStale);
        Assert.Equal("данные 2 мин назад", panel.AgeText);
    }

    [Fact]
    public void Tooltip_summarizes_the_worst_of_three_headroom()
    {
        var panel = UsagePresenter.Map(StateWith(
            Limit(LimitKind.Session, 59),
            Limit(LimitKind.WeeklyAll, 88),
            Limit(LimitKind.WeeklyScoped, 91)));

        // Mirrors the donut arc (worst-of-three), not per-limit detail — that lives in the panel.
        Assert.Equal("Claude Code: 59% свободно", panel.TooltipText);
    }

    [Fact]
    public void Tooltip_on_total_failure_says_no_data()
    {
        var state = new UsageState(
            Provider,
            Snapshot: null,
            Error: new FetchError(FetchErrorKind.NoCredentials, "Нет учётных данных"),
            FetchedAt: null,
            Now: Now,
            StaleAfter: TimeSpan.FromSeconds(90));

        Assert.Equal("Claude Code: нет данных", UsagePresenter.Map(state).TooltipText);
    }

    // --- helpers ---------------------------------------------------------

    private static Limit Limit(LimitKind kind, double headroom)
        => new(kind, kind.ToString(), headroom, Now.AddHours(5), kind == LimitKind.Session);

    private static UsageState StateWith(params Limit[] limits)
        => new(Provider, new UsageSnapshot(limits, Now), Error: null, FetchedAt: Now, Now: Now,
            StaleAfter: TimeSpan.FromSeconds(90));
}
