using System.Diagnostics;
using Microsoft.Extensions.Time.Testing;
using WidgetSubscription.Core;
using Xunit;

namespace WidgetSubscription.Tests;

/// <summary>
/// Seam 1 (spec #8): the Core update engine driven through a fake <see cref="IUsageProvider"/>
/// under a <see cref="FakeTimeProvider"/>. Covers cache/age/staleness, poll cadence, the
/// force-on-open debounce, failure backoff with Retry-After, single-flight, and clean shutdown.
/// </summary>
public sealed class UsageMonitorTests
{
    private static readonly RefreshOptions Options = RefreshOptions.Default;

    [Fact]
    public async Task Success_caches_snapshot_clears_error_and_resets_delay()
    {
        var time = new FakeTimeProvider(Origin);
        var provider = new FakeProvider(time, Outcome.Ok());
        using var monitor = new UsageMonitor(provider, time, Options);

        await monitor.RefreshNowAsync(CancellationToken.None);

        var state = monitor.Current;
        Assert.True(state.HasSnapshot);
        Assert.False(state.IsDegraded);
        Assert.Equal(3, state.Snapshot!.Limits.Count);
        Assert.Equal(Options.PollInterval, monitor.NextDelay);
    }

    [Fact]
    public async Task Failure_records_error_and_keeps_previous_snapshot()
    {
        var time = new FakeTimeProvider(Origin);
        var provider = new FakeProvider(time, Outcome.Ok(), Outcome.Fail(FetchErrorKind.SourceUnavailable));
        using var monitor = new UsageMonitor(provider, time, Options);

        await monitor.RefreshNowAsync(CancellationToken.None);
        await monitor.RefreshNowAsync(CancellationToken.None);

        var state = monitor.Current;
        Assert.True(state.HasSnapshot);      // last good snapshot survives
        Assert.True(state.IsDegraded);       // but the latest fetch failed
        Assert.Equal(FetchErrorKind.SourceUnavailable, state.Error!.Kind);
    }

    [Fact]
    public async Task Backoff_grows_exponentially_and_caps_at_max()
    {
        var time = new FakeTimeProvider(Origin);
        var provider = new FakeProvider(time,
            Outcome.Fail(FetchErrorKind.SourceUnavailable),
            Outcome.Fail(FetchErrorKind.SourceUnavailable),
            Outcome.Fail(FetchErrorKind.SourceUnavailable),
            Outcome.Fail(FetchErrorKind.SourceUnavailable));
        using var monitor = new UsageMonitor(provider, time, Options);

        var delays = new List<TimeSpan>();
        for (var i = 0; i < 4; i++)
        {
            await monitor.RefreshNowAsync(CancellationToken.None);
            delays.Add(monitor.NextDelay);
        }

        Assert.Equal(
            new[] { 60, 120, 240, 300 }.Select(s => TimeSpan.FromSeconds(s)),
            delays);
    }

    [Fact]
    public async Task RetryAfter_longer_than_backoff_is_respected()
    {
        var time = new FakeTimeProvider(Origin);
        var provider = new FakeProvider(time,
            Outcome.Fail(FetchErrorKind.SourceUnavailable, TimeSpan.FromSeconds(400)));
        using var monitor = new UsageMonitor(provider, time, Options);

        await monitor.RefreshNowAsync(CancellationToken.None);

        Assert.Equal(TimeSpan.FromSeconds(400), monitor.NextDelay);   // beyond the 300 cap
    }

    [Fact]
    public async Task Recovery_after_failure_resets_streak_and_delay()
    {
        var time = new FakeTimeProvider(Origin);
        var provider = new FakeProvider(time,
            Outcome.Fail(FetchErrorKind.SourceUnavailable),
            Outcome.Fail(FetchErrorKind.SourceUnavailable),
            Outcome.Ok());
        using var monitor = new UsageMonitor(provider, time, Options);

        await monitor.RefreshNowAsync(CancellationToken.None);
        await monitor.RefreshNowAsync(CancellationToken.None);
        await monitor.RefreshNowAsync(CancellationToken.None);

        Assert.False(monitor.Current.IsDegraded);
        Assert.Equal(Options.PollInterval, monitor.NextDelay);
    }

    [Fact]
    public async Task Snapshot_goes_stale_past_the_threshold_but_countdown_uses_absolute_reset()
    {
        var time = new FakeTimeProvider(Origin);
        var provider = new FakeProvider(time, Outcome.Ok());
        using var monitor = new UsageMonitor(provider, time, Options);

        await monitor.RefreshNowAsync(CancellationToken.None);
        Assert.False(monitor.Current.IsStale);

        time.Advance(TimeSpan.FromSeconds(120));    // past the 90s stale threshold

        var state = monitor.Current;
        Assert.True(state.IsStale);
        Assert.Equal(TimeSpan.FromSeconds(120), state.Age);
        // ResetsAt is absolute, so it is unaffected by snapshot age.
        Assert.Equal(Origin.AddHours(5), state.Snapshot!.Limits[0].ResetsAt);
    }

    [Fact]
    public async Task Open_refresh_fetches_when_stale_and_debounces_when_fresh()
    {
        var time = new FakeTimeProvider(Origin);
        var provider = new FakeProvider(time, Outcome.Ok(), Outcome.Ok());
        using var monitor = new UsageMonitor(provider, time, Options);

        await monitor.RefreshOnOpenAsync(CancellationToken.None);   // nothing cached -> fetch
        Assert.Equal(1, provider.Calls);

        time.Advance(TimeSpan.FromSeconds(10));                     // still fresh (< 30s)
        await monitor.RefreshOnOpenAsync(CancellationToken.None);
        Assert.Equal(1, provider.Calls);                           // debounced

        time.Advance(TimeSpan.FromSeconds(40));                     // now 50s old (> 30s)
        await monitor.RefreshOnOpenAsync(CancellationToken.None);
        Assert.Equal(2, provider.Calls);                           // forced
    }

    [Fact]
    public async Task Only_one_fetch_runs_at_a_time()
    {
        var time = new FakeTimeProvider(Origin);
        var provider = new FakeProvider(time, Outcome.Ok(), Outcome.Ok());
        var gate = new TaskCompletionSource();
        provider.Gate = () => gate.Task;
        using var monitor = new UsageMonitor(provider, time, Options);

        var first = monitor.RefreshNowAsync(CancellationToken.None);
        var second = monitor.RefreshNowAsync(CancellationToken.None);

        await WaitUntil(() => provider.Calls >= 1);
        await Task.Delay(50);                        // give the second call a chance to (wrongly) enter
        Assert.Equal(1, provider.Calls);            // second is held at the semaphore
        Assert.Equal(1, provider.MaxConcurrent);

        gate.SetResult();
        await Task.WhenAll(first, second);
        Assert.Equal(2, provider.Calls);
        Assert.Equal(1, provider.MaxConcurrent);
    }

    [Fact]
    public async Task Loop_polls_on_the_configured_cadence()
    {
        var time = new FakeTimeProvider(Origin);
        var provider = new FakeProvider(time, Outcome.Ok(), Outcome.Ok(), Outcome.Ok());
        using var monitor = new UsageMonitor(provider, time, Options);

        monitor.Start();
        await WaitUntil(() => provider.Calls >= 1);   // immediate first poll
        await Task.Delay(100);                        // let the loop reach its Delay await

        time.Advance(Options.PollInterval);
        await WaitUntil(() => provider.Calls >= 2);   // next tick fired
    }

    [Fact]
    public async Task Dispose_stops_the_loop()
    {
        var time = new FakeTimeProvider(Origin);
        var provider = new FakeProvider(time, Outcome.Ok(), Outcome.Ok(), Outcome.Ok());
        var monitor = new UsageMonitor(provider, time, Options);

        monitor.Start();
        await WaitUntil(() => provider.Calls >= 1);
        await Task.Delay(100);
        monitor.Dispose();
        var callsAtDispose = provider.Calls;

        time.Advance(TimeSpan.FromMinutes(10));
        await Task.Delay(100);

        Assert.Equal(callsAtDispose, provider.Calls);
    }

    // --- helpers ---------------------------------------------------------

    private static readonly DateTimeOffset Origin = new(2026, 7, 21, 12, 0, 0, TimeSpan.Zero);

    private static async Task WaitUntil(Func<bool> condition, int timeoutMs = 3000)
    {
        var sw = Stopwatch.StartNew();
        while (!condition())
        {
            if (sw.ElapsedMilliseconds > timeoutMs)
                throw new TimeoutException("Condition not met in time.");
            await Task.Delay(10);
        }
    }

    private sealed record Outcome(bool IsSuccess, FetchErrorKind ErrorKind, TimeSpan? RetryAfter)
    {
        public static Outcome Ok() => new(true, default, null);
        public static Outcome Fail(FetchErrorKind kind, TimeSpan? retryAfter = null)
            => new(false, kind, retryAfter);
    }

    private sealed class FakeProvider : IUsageProvider
    {
        private readonly TimeProvider _time;
        private readonly Queue<Outcome> _outcomes;
        private readonly Outcome _last;
        private int _calls;
        private int _inFlight;

        public FakeProvider(TimeProvider time, params Outcome[] outcomes)
        {
            _time = time;
            _outcomes = new Queue<Outcome>(outcomes);
            _last = outcomes.Length > 0 ? outcomes[^1] : Outcome.Fail(FetchErrorKind.SourceUnavailable);
        }

        public ProviderInfo Info { get; } = new("claude-code", "Claude Code", "#D97757");
        public int Calls => Volatile.Read(ref _calls);
        public int MaxConcurrent { get; private set; }
        public Func<Task>? Gate { get; set; }

        public async Task<FetchResult> FetchAsync(CancellationToken ct)
        {
            Interlocked.Increment(ref _calls);
            var concurrent = Interlocked.Increment(ref _inFlight);
            MaxConcurrent = Math.Max(MaxConcurrent, concurrent);
            try
            {
                if (Gate is not null)
                    await Gate().ConfigureAwait(false);

                var outcome = _outcomes.Count > 0 ? _outcomes.Dequeue() : _last;
                if (!outcome.IsSuccess)
                    return new FetchResult.Failure(new FetchError(outcome.ErrorKind, "boom", outcome.RetryAfter));

                var reset = _time.GetUtcNow().AddHours(5);
                var limits = new[]
                {
                    new Limit(LimitKind.Session, "5-hour", 85, reset, true),
                    new Limit(LimitKind.WeeklyAll, "Weekly", 98, reset, false),
                    new Limit(LimitKind.WeeklyScoped, "Fable 5", 98, reset, false),
                };
                return new FetchResult.Success(new UsageSnapshot(limits, _time.GetUtcNow()));
            }
            finally
            {
                Interlocked.Decrement(ref _inFlight);
            }
        }
    }
}
