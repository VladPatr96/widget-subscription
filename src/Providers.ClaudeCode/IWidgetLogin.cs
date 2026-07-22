namespace WidgetSubscription.Providers.ClaudeCode;

/// <summary>
/// The interactive own-login seam (#18). <see cref="LoginAsync"/> runs the full OAuth/PKCE flow
/// (external browser, loopback capture with a hosted-paste fallback, code exchange) and persists
/// the resulting grant; <see cref="SignOut"/> discards it. Living beside the adapter, it is the
/// write side that <see cref="ICredentialSource"/> deliberately is not (#17 §3).
/// </summary>
public interface IWidgetLogin
{
    Task<LoginResult> LoginAsync(CancellationToken ct);

    void SignOut();
}

/// <summary>Outcome of a login attempt: signed in (with the account label, if any), user-cancelled, or failed.</summary>
public abstract record LoginResult
{
    public sealed record Success(string? Account) : LoginResult;
    public sealed record Cancelled : LoginResult;
    public sealed record Failed(string Message) : LoginResult;

    private LoginResult() { }
}

/// <summary>Opens a URL in the user's default system browser — external, not a WebView (#18 §2).</summary>
public interface IBrowserLauncher
{
    void Open(string url);
}

/// <summary>Binds an ephemeral loopback port for the OAuth redirect (#18 §1a); returns <c>null</c> when no port binds.</summary>
public interface ILoopbackListenerFactory
{
    ILoopbackListener? TryStart();
}

/// <summary>A bound loopback endpoint awaiting exactly one OAuth redirect.</summary>
public interface ILoopbackListener : IDisposable
{
    /// <summary>The <c>redirect_uri</c> to send to the authorize endpoint (e.g. <c>http://localhost:PORT/callback</c>).</summary>
    string RedirectUri { get; }

    /// <summary>Awaits the browser redirect and returns its <c>code</c>/<c>state</c>/<c>error</c>.</summary>
    Task<LoopbackCallback> WaitForCallbackAsync(CancellationToken ct);
}

/// <summary>The query parameters carried by the OAuth redirect.</summary>
public sealed record LoopbackCallback(string? Code, string? State, string? Error);

/// <summary>
/// The hosted-paste fallback (#18 §1b/§3.3): prompts the user to paste the code shown by the hosted
/// callback (optionally in <c>CODE#STATE</c> form). Returns <c>null</c> when the user cancels.
/// </summary>
public interface ICodeEntry
{
    Task<string?> PromptAsync(CancellationToken ct);
}
