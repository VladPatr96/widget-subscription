namespace WidgetSubscription.Providers.ClaudeCode;

/// <summary>
/// Shared constants for the claude.ai subscription OAuth grant (research #14). The client is
/// public (Authorization Code + PKCE, no secret); the same hard-coded <see cref="ClientId"/> is
/// used by Claude Code and every reimplementation of the flow. Used by both the own-login
/// refresh (<see cref="OwnLoginCredentialSource"/>) and, later, the interactive login.
/// </summary>
internal static class ClaudeOAuth
{
    public const string ClientId = "9d1c250a-e61b-44d9-88ed-5944d1962f5e";

    /// <summary>Token endpoint for authorization_code exchange and refresh_token rotation (#14 §4–5).</summary>
    public const string TokenEndpoint = "https://console.anthropic.com/v1/oauth/token";

    /// <summary>Authorize endpoint carrying subscription limits (claude.ai, not console) (#14 §2).</summary>
    public const string AuthorizeEndpoint = "https://claude.ai/oauth/authorize";

    /// <summary>Least-privilege scope for a read-only usage widget — api-key minting dropped (#18 §6).</summary>
    public const string Scope = "user:inference user:profile";
}
