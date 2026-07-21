using WidgetSubscription.Core;
using WidgetSubscription.Providers.ClaudeCode;

namespace WidgetSubscription.App;

/// <summary>
/// Headless end-to-end smoke check (run with <c>--selftest</c>). Exercises the full pipeline —
/// presentation mapping and the SkiaSharp donut renderer over representative states, plus the
/// real provider path against the live credential source — and validates the rendered PNGs,
/// without opening any window. Prints a summary and returns 0 on success, 1 on failure.
/// </summary>
public static class SelfTest
{
    private static readonly byte[] PngSignature = { 0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A };
    private static readonly DateTimeOffset Now = DateTimeOffset.UtcNow;
    private static readonly ProviderInfo Provider = new("claude-code", "Claude Code", "#D97757");

    public static async Task<int> RunAsync()
    {
        var ok = true;

        Console.WriteLine("== Presentation + renderer over representative states ==");
        foreach (var (label, state) in SampleStates())
        {
            var view = UsagePresenter.Map(state);
            var png = DonutRenderer.RenderPng(view.Icon, 32);
            var valid = IsPng(png);
            ok &= valid;
            Console.WriteLine(
                $"[{label}] worst={view.Icon.WorstHeadroom,3:0}% color={view.Icon.Color} " +
                $"degraded={view.IsDegraded} rows={view.Limits.Count} png={png.Length}B valid={valid}");
            foreach (var limit in view.Limits)
                Console.WriteLine($"    {limit.DisplayName,-9} {limit.Headroom,3:0}% [{limit.StatusLabel}] {limit.ResetText}");
        }

        Console.WriteLine();
        Console.WriteLine("== Live provider path (real credentials, read-only) ==");
        using var http = new HttpClient { Timeout = TimeSpan.FromSeconds(15) };
        var provider = new ClaudeCodeAdapter(http, new ClaudeCredentialsFileSource());
        using var monitor = new UsageMonitor(provider);
        await monitor.RefreshNowAsync(CancellationToken.None);
        var live = UsagePresenter.Map(monitor.Current);
        var livePng = DonutRenderer.RenderPng(live.Icon, 32);
        ok &= IsPng(livePng);
        Console.WriteLine(
            $"degraded={live.IsDegraded} reason={live.DegradedReason ?? "-"} rows={live.Limits.Count} " +
            $"worst={live.Icon.WorstHeadroom:0}% png={livePng.Length}B valid={IsPng(livePng)}");
        foreach (var limit in live.Limits)
            Console.WriteLine($"    {limit.DisplayName,-9} {limit.Headroom,3:0}% [{limit.StatusLabel}] {limit.ResetText}");

        Console.WriteLine();
        Console.WriteLine(ok ? "SELFTEST OK" : "SELFTEST FAILED");
        return ok ? 0 : 1;
    }

    private static IEnumerable<(string Label, UsageState State)> SampleStates()
    {
        yield return ("healthy", Success(85, 98, 98));
        yield return ("one-close", Success(12, 60, 34));
        yield return ("one-exhausted", Success(0, 41, 4));
        yield return ("degraded", new UsageState(
            Provider,
            new UsageSnapshot(Limits(85, 98, 98), Now),
            new FetchError(FetchErrorKind.SourceUnavailable, "Источник Claude Code недоступен"),
            Now, Now, TimeSpan.FromSeconds(90)));
        yield return ("no-credentials", new UsageState(
            Provider, Snapshot: null,
            new FetchError(FetchErrorKind.NoCredentials, "Нет учётных данных Claude Code"),
            FetchedAt: null, Now, TimeSpan.FromSeconds(90)));
    }

    private static UsageState Success(double session, double weeklyAll, double fable)
        => new(Provider, new UsageSnapshot(Limits(session, weeklyAll, fable), Now),
            Error: null, FetchedAt: Now, Now: Now, StaleAfter: TimeSpan.FromSeconds(90));

    private static Limit[] Limits(double session, double weeklyAll, double fable) => new[]
    {
        new Limit(LimitKind.Session, "5-hour", session, Now.AddHours(3), true),
        new Limit(LimitKind.WeeklyAll, "Weekly", weeklyAll, Now.AddDays(4), false),
        new Limit(LimitKind.WeeklyScoped, "Fable 5", fable, Now.AddDays(4), false),
    };

    private static bool IsPng(byte[] bytes)
        => bytes.Length > PngSignature.Length && bytes.Take(PngSignature.Length).SequenceEqual(PngSignature);
}
