namespace WidgetSubscription.Providers.ClaudeCode;

/// <summary>
/// Token acquisition, injected and living beside the Claude Code adapter rather than
/// as a port in <c>Core</c> (decision B). Keeps <see cref="Core.IUsageProvider"/> free
/// of tokens/files/OAuth and gives a seam for tests (a fake without HTTP) and for
/// mobile/other providers (a different source behind the same interface).
/// The MVP implementation (<c>ClaudeCredentialsFileSource</c>, read-only over
/// <c>~/.claude/.credentials.json</c>) is added in issue #2.
/// </summary>
public interface ICredentialSource
{
    /// <summary>Returns the current access token, or <c>null</c> when credentials are unavailable.</summary>
    Task<AccessToken?> GetAsync(CancellationToken ct);
}

/// <summary>
/// Optional role a credential source may also implement: a hint that the last token it handed out
/// was rejected (HTTP 401), so any cached token should be dropped and the next <see
/// cref="ICredentialSource.GetAsync"/> must re-acquire. The adapter calls this on its 401 path
/// before the single reread+retry. The borrow source does not implement it — rereading the file
/// already picks up a token Claude Code refreshed — while the own-login source uses it to force a
/// refresh of a server-revoked token that has not yet reached its local expiry (spec §4.3).
/// </summary>
public interface ICredentialInvalidation
{
    void Invalidate();
}

/// <summary>An access token and its optional expiry, as read from the provider's credentials.</summary>
public sealed record AccessToken(string Value, DateTimeOffset? ExpiresAt);
