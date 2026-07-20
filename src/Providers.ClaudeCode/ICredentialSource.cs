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

/// <summary>An access token and its optional expiry, as read from the provider's credentials.</summary>
public sealed record AccessToken(string Value, DateTimeOffset? ExpiresAt);
