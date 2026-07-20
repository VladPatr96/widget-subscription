namespace WidgetSubscription.Core;

/// <summary>
/// The single contract <c>Core</c> knows about a usage provider. UI-agnostic:
/// it says nothing about UI, tokens, files, OAuth, or HTTP. Each provider (Claude
/// Code first and only in the MVP) implements this port in its own assembly.
/// </summary>
public interface IUsageProvider
{
    /// <summary>Static identity, available without a network call and even on <see cref="FetchResult.Failure"/>.</summary>
    ProviderInfo Info { get; }

    /// <summary>Fetches the current usage snapshot. Routine failures are data (<see cref="FetchResult"/>), not exceptions.</summary>
    Task<FetchResult> FetchAsync(CancellationToken ct);
}

/// <summary>Static provider identity, drawn even when a fetch fails (e.g. the grey donut).</summary>
/// <param name="Id">Stable string key (not an enum: providers are the extension point).</param>
/// <param name="DisplayName">Human-facing provider name.</param>
/// <param name="BrandColor">Brand hex color, not a state color.</param>
public sealed record ProviderInfo(string Id, string DisplayName, string BrandColor);

/// <summary>
/// Result envelope: routine failures are data, not exceptions. Closed hierarchy —
/// exactly <see cref="Success"/> or <see cref="Failure"/>.
/// </summary>
public abstract record FetchResult
{
    public sealed record Success(UsageSnapshot Snapshot) : FetchResult;

    public sealed record Failure(FetchError Error) : FetchResult;

    private FetchResult() { }
}

/// <summary>A point-in-time view of every <see cref="Limit"/> plus when the port produced it.</summary>
/// <param name="Limits">The provider's limits.</param>
/// <param name="FetchedAt">Stamped by the port at the moment of the real response.</param>
public sealed record UsageSnapshot(IReadOnlyList<Limit> Limits, DateTimeOffset FetchedAt);

/// <summary>
/// One quota window. Carries <see cref="Headroom"/> (0..100, already 100−percent), not
/// percent. No <c>severity</c> (thresholds/colors are the presentation layer) and no
/// <c>Scope</c> (Fable is <see cref="LimitKind.WeeklyScoped"/> + <see cref="DisplayName"/> "Fable 5").
/// </summary>
/// <param name="Kind">The kind of quota window.</param>
/// <param name="DisplayName">Human-facing limit name, supplied by the adapter.</param>
/// <param name="Headroom">Remaining amount, 0..100 (already 100−percent).</param>
/// <param name="ResetsAt">Absolute reset time; the "in N" countdown is computed by the UI.</param>
/// <param name="IsActive">Whether this limit is the binding one right now.</param>
public sealed record Limit(
    LimitKind Kind,
    string DisplayName,
    double Headroom,
    DateTimeOffset ResetsAt,
    bool IsActive);

public enum LimitKind { Session, WeeklyAll, WeeklyScoped }

/// <summary>An exhausted failure outcome, not each internal attempt.</summary>
/// <param name="Kind">The failure category.</param>
/// <param name="Message">Human-readable reason, for the panel.</param>
/// <param name="RetryAfter">From a <c>Retry-After</c> header, if present; backoff is computed by the update layer.</param>
public sealed record FetchError(FetchErrorKind Kind, string Message, TimeSpan? RetryAfter = null);

public enum FetchErrorKind { NoCredentials, Unauthorized, SourceUnavailable, Timeout, Malformed }
