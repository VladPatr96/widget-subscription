namespace WidgetSubscription.Core;

/// <summary>
/// Timing policy for the update engine (spec #5). All values are injectable so tests can
/// pin them; <see cref="Default"/> carries the MVP numbers.
/// </summary>
public sealed record RefreshOptions(
    TimeSpan PollInterval,
    TimeSpan ForceOnOpenThreshold,
    TimeSpan StaleAfter,
    TimeSpan MinBackoff,
    TimeSpan MaxBackoff)
{
    public static RefreshOptions Default { get; } = new(
        PollInterval: TimeSpan.FromSeconds(60),
        ForceOnOpenThreshold: TimeSpan.FromSeconds(30),
        StaleAfter: TimeSpan.FromSeconds(90),
        MinBackoff: TimeSpan.FromSeconds(60),
        MaxBackoff: TimeSpan.FromSeconds(300));

    /// <summary>
    /// The delay before the next poll after <paramref name="failureStreak"/> consecutive
    /// failures: exponential from <see cref="MinBackoff"/>, capped at <see cref="MaxBackoff"/>,
    /// but never shorter than a server-supplied <paramref name="retryAfter"/>.
    /// </summary>
    public TimeSpan BackoffFor(int failureStreak, TimeSpan? retryAfter)
    {
        var exponent = Math.Max(0, failureStreak - 1);
        var seconds = MinBackoff.TotalSeconds * Math.Pow(2, exponent);
        var backoff = TimeSpan.FromSeconds(Math.Min(seconds, MaxBackoff.TotalSeconds));
        return retryAfter is { } after && after > backoff ? after : backoff;
    }
}

/// <summary>
/// An immutable view of what the engine currently knows, handed to the presentation layer.
/// Holds the last successful <see cref="Snapshot"/> and the last <see cref="Error"/> (non-null
/// only when the most recent fetch failed), plus the clock reading used for age/countdown.
/// </summary>
public sealed record UsageState(
    ProviderInfo Provider,
    UsageSnapshot? Snapshot,
    FetchError? Error,
    DateTimeOffset? FetchedAt,
    DateTimeOffset Now,
    TimeSpan StaleAfter)
{
    /// <summary>Age of the cached snapshot, or <c>null</c> if nothing has been fetched yet.</summary>
    public TimeSpan? Age => FetchedAt is { } fetchedAt ? Now - fetchedAt : null;

    /// <summary>Whether the cached snapshot is older than the stale threshold.</summary>
    public bool IsStale => Age is { } age && age > StaleAfter;

    /// <summary>Whether the most recent fetch failed (grey/degraded presentation).</summary>
    public bool IsDegraded => Error is not null;

    public bool HasSnapshot => Snapshot is not null;
}

/// <summary>
/// The UI-agnostic update engine (spec #5). Polls an <see cref="IUsageProvider"/> on a cadence,
/// force-refreshes on panel open when the snapshot is stale, caches the last snapshot in memory
/// (no disk), and backs off on failure while respecting <c>Retry-After</c>. Time and scheduling
/// come from an injected <see cref="TimeProvider"/> for deterministic tests. A single
/// <see cref="SemaphoreSlim"/> guarantees at most one fetch in flight, so a poll tick and an
/// open-refresh can never overlap.
/// </summary>
public sealed class UsageMonitor : IDisposable
{
    private readonly IUsageProvider _provider;
    private readonly TimeProvider _time;
    private readonly RefreshOptions _options;
    private readonly SemaphoreSlim _fetchLock = new(1, 1);
    private readonly object _gate = new();

    private UsageSnapshot? _snapshot;
    private DateTimeOffset? _fetchedAt;
    private FetchError? _error;
    private int _failureStreak;
    private TimeSpan _nextDelay;
    private CancellationTokenSource? _loopCts;
    private Task? _loop;

    public UsageMonitor(IUsageProvider provider, TimeProvider? time = null, RefreshOptions? options = null)
    {
        _provider = provider;
        _time = time ?? TimeProvider.System;
        _options = options ?? RefreshOptions.Default;
        _nextDelay = _options.PollInterval;
    }

    /// <summary>Raised after every fetch attempt, whether it succeeded or failed.</summary>
    public event EventHandler? Updated;

    /// <summary>The current state, stamped with the clock's "now" for age/countdown.</summary>
    public UsageState Current
    {
        get
        {
            lock (_gate)
            {
                return new UsageState(
                    _provider.Info, _snapshot, _error, _fetchedAt,
                    _time.GetUtcNow(), _options.StaleAfter);
            }
        }
    }

    /// <summary>The delay the engine will wait before its next scheduled poll.</summary>
    public TimeSpan NextDelay
    {
        get { lock (_gate) return _nextDelay; }
    }

    /// <summary>Starts the background poll loop. Idempotent.</summary>
    public void Start()
    {
        lock (_gate)
        {
            if (_loop is not null)
                return;
            _loopCts = new CancellationTokenSource();
            _loop = RunLoopAsync(_loopCts.Token);
        }
    }

    /// <summary>Fetches once now, updating cache and backoff. Serialized against any other fetch.</summary>
    public async Task RefreshNowAsync(CancellationToken ct)
    {
        await _fetchLock.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            await FetchAndStoreAsync(ct).ConfigureAwait(false);
        }
        finally
        {
            _fetchLock.Release();
        }
    }

    /// <summary>
    /// Called when the panel opens: fetches only if the snapshot is missing or older than
    /// <see cref="RefreshOptions.ForceOnOpenThreshold"/>. Rechecks under the fetch lock so a poll
    /// that landed while we waited debounces this force-refresh.
    /// </summary>
    public async Task RefreshOnOpenAsync(CancellationToken ct)
    {
        if (!ShouldForceOnOpen())
            return;

        await _fetchLock.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            if (!ShouldForceOnOpen())
                return;
            await FetchAndStoreAsync(ct).ConfigureAwait(false);
        }
        finally
        {
            _fetchLock.Release();
        }
    }

    private bool ShouldForceOnOpen()
    {
        lock (_gate)
        {
            if (_fetchedAt is not { } fetchedAt)
                return true;
            return _time.GetUtcNow() - fetchedAt > _options.ForceOnOpenThreshold;
        }
    }

    private async Task FetchAndStoreAsync(CancellationToken ct)
    {
        var result = await _provider.FetchAsync(ct).ConfigureAwait(false);
        lock (_gate)
        {
            switch (result)
            {
                case FetchResult.Success success:
                    _snapshot = success.Snapshot;
                    _fetchedAt = success.Snapshot.FetchedAt;
                    _error = null;
                    _failureStreak = 0;
                    _nextDelay = _options.PollInterval;
                    break;
                case FetchResult.Failure failure:
                    _error = failure.Error;
                    _failureStreak++;
                    _nextDelay = _options.BackoffFor(_failureStreak, failure.Error.RetryAfter);
                    break;
            }
        }
        Updated?.Invoke(this, EventArgs.Empty);
    }

    private async Task RunLoopAsync(CancellationToken ct)
    {
        try
        {
            while (!ct.IsCancellationRequested)
            {
                await RefreshNowAsync(ct).ConfigureAwait(false);
                TimeSpan delay;
                lock (_gate)
                    delay = _nextDelay;
                await Task.Delay(delay, _time, ct).ConfigureAwait(false);
            }
        }
        catch (OperationCanceledException)
        {
            // Expected on Dispose.
        }
    }

    public void Dispose()
    {
        CancellationTokenSource? cts;
        Task? loop;
        lock (_gate)
        {
            cts = _loopCts;
            loop = _loop;
            _loopCts = null;
            _loop = null;
        }

        cts?.Cancel();
        try
        {
            loop?.GetAwaiter().GetResult();
        }
        catch (OperationCanceledException)
        {
            // Expected.
        }
        cts?.Dispose();
        _fetchLock.Dispose();
    }
}
