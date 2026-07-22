namespace WidgetSubscription.Providers.ClaudeCode;

/// <summary>
/// Persistence for the widget's <em>own</em> OAuth grant (own-login mode), living beside
/// the Claude Code adapter rather than as a port in <c>Core</c> (auth-port decision, #17).
/// It owns only the widget's own token file — it never reads or writes Claude Code's
/// <c>~/.claude/.credentials.json</c> (grant families are kept separate, #14 §6). The
/// interactive login and refresh live in the own-login credential source; this seam is
/// only load/save/clear so it can be faked without touching disk in tests (#19 §3).
/// </summary>
public interface IWidgetTokenStore
{
    /// <summary>Returns the stored tokens, or <c>null</c> when absent or unreadable/corrupt.</summary>
    WidgetTokens? Load();

    /// <summary>Persists the token set, replacing any existing one atomically.</summary>
    void Save(WidgetTokens tokens);

    /// <summary>Removes the stored tokens (used on sign-out and on a terminal refresh failure).</summary>
    void Clear();
}

/// <summary>
/// The widget's own OAuth grant. Distinct from Claude Code's tokens: access + rotating
/// refresh (#14), each with its absolute expiry, plus the granted scope.
/// </summary>
public sealed record WidgetTokens(
    string AccessToken,
    string RefreshToken,
    DateTimeOffset ExpiresAt,
    DateTimeOffset RefreshTokenExpiresAt,
    string Scope);
