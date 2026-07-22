using System.Text;
using System.Text.Json;

namespace WidgetSubscription.Providers.ClaudeCode;

/// <summary>
/// Orchestrates the interactive own-login flow (#18). Mints a fresh <see cref="Pkce"/>, opens the
/// authorize URL in the external browser, obtains the code via loopback capture (primary) or the
/// hosted-paste fallback, validates <c>state</c>, exchanges the code for tokens, and persists them
/// through <see cref="IWidgetTokenStore"/>. All I/O edges (browser, loopback, paste prompt) are
/// injected seams so the orchestration is testable; only the code exchange is HTTP here.
/// </summary>
public sealed class WidgetLogin : IWidgetLogin
{
    private const string HostedCallback = "https://console.anthropic.com/oauth/code/callback";

    private readonly HttpClient _http;
    private readonly IWidgetTokenStore _store;
    private readonly IBrowserLauncher _browser;
    private readonly ILoopbackListenerFactory _loopback;
    private readonly ICodeEntry _codeEntry;
    private readonly TimeProvider _time;
    private readonly TimeSpan _timeout;

    public WidgetLogin(
        HttpClient http,
        IWidgetTokenStore store,
        IBrowserLauncher browser,
        ILoopbackListenerFactory loopback,
        ICodeEntry codeEntry,
        TimeProvider? time = null,
        TimeSpan? timeout = null)
    {
        _http = http;
        _store = store;
        _browser = browser;
        _loopback = loopback;
        _codeEntry = codeEntry;
        _time = time ?? TimeProvider.System;
        _timeout = timeout ?? TimeSpan.FromMinutes(3);
    }

    public void SignOut() => _store.Clear();

    public async Task<LoginResult> LoginAsync(CancellationToken ct)
    {
        var pkce = Pkce.Create();
        try
        {
            Obtained? obtained;
            using (var listener = _loopback.TryStart())
                obtained = listener is null ? null : await TryLoopback(listener, pkce, ct).ConfigureAwait(false);

            // No loopback (port busy) or it timed out / errored ⇒ hosted-paste fallback.
            obtained ??= await TryPaste(pkce, ct).ConfigureAwait(false);
            if (obtained is null)
                return new LoginResult.Cancelled();

            var got = obtained.Value;
            if (got.State is { } state && !string.Equals(state, pkce.State, StringComparison.Ordinal))
                return new LoginResult.Failed("Не удалось подтвердить вход (несовпадение state). Попробуйте снова.");

            return await ExchangeAndPersist(got, pkce, ct).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            return new LoginResult.Cancelled();
        }
    }

    private async Task<Obtained?> TryLoopback(ILoopbackListener listener, Pkce pkce, CancellationToken ct)
    {
        _browser.Open(BuildAuthorizeUrl(listener.RedirectUri, pkce, hostedPaste: false));

        using var timeoutCts = new CancellationTokenSource(_timeout, _time);
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(ct, timeoutCts.Token);
        LoopbackCallback callback;
        try
        {
            callback = await listener.WaitForCallbackAsync(linked.Token).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            if (ct.IsCancellationRequested)
                throw;      // user cancel ⇒ surface as Cancelled
            return null;    // timeout ⇒ fall back to paste
        }

        if (!string.IsNullOrEmpty(callback.Error) || string.IsNullOrEmpty(callback.Code))
            return null;    // provider error / empty ⇒ fall back to paste

        return new Obtained(callback.Code!, callback.State, listener.RedirectUri);
    }

    private async Task<Obtained?> TryPaste(Pkce pkce, CancellationToken ct)
    {
        _browser.Open(BuildAuthorizeUrl(HostedCallback, pkce, hostedPaste: true));

        var pasted = await _codeEntry.PromptAsync(ct).ConfigureAwait(false);
        if (string.IsNullOrWhiteSpace(pasted))
            return null;    // user cancelled the paste prompt

        // The hosted callback sometimes renders the code as CODE#STATE.
        var parts = pasted.Trim().Split('#', 2);
        var state = parts.Length > 1 ? parts[1] : null;
        return new Obtained(parts[0], state, HostedCallback);
    }

    private async Task<LoginResult> ExchangeAndPersist(Obtained got, Pkce pkce, CancellationToken ct)
    {
        HttpResponseMessage response;
        try
        {
            response = await _http.SendAsync(BuildExchangeRequest(got, pkce), ct).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex) when (ex is OperationCanceledException or HttpRequestException)
        {
            return new LoginResult.Failed("Не удалось связаться с Anthropic. Повторите.");
        }

        using (response)
        {
            if (!response.IsSuccessStatusCode)
                return new LoginResult.Failed($"Обмен кода не удался ({(int)response.StatusCode}). Повторите.");

            string body;
            try
            {
                body = await response.Content.ReadAsStringAsync(ct).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested)
            {
                throw;
            }
            catch (Exception ex) when (ex is OperationCanceledException or HttpRequestException)
            {
                return new LoginResult.Failed("Не удалось прочитать ответ Anthropic. Повторите.");
            }

            var parsed = ParseTokens(body);
            if (parsed is null)
                return new LoginResult.Failed("Некорректный ответ сервера. Повторите.");

            _store.Save(parsed.Value.Tokens);
            return new LoginResult.Success(parsed.Value.Account);
        }
    }

    private static string BuildAuthorizeUrl(string redirectUri, Pkce pkce, bool hostedPaste)
    {
        var query = new Dictionary<string, string>
        {
            ["client_id"] = ClaudeOAuth.ClientId,
            ["response_type"] = "code",
            ["redirect_uri"] = redirectUri,
            ["scope"] = ClaudeOAuth.Scope,
            ["code_challenge"] = pkce.Challenge,
            ["code_challenge_method"] = "S256",
            ["state"] = pkce.State,
        };
        if (hostedPaste)
            query["code"] = "true";

        var encoded = string.Join("&", query.Select(
            kv => $"{Uri.EscapeDataString(kv.Key)}={Uri.EscapeDataString(kv.Value)}"));
        return $"{ClaudeOAuth.AuthorizeEndpoint}?{encoded}";
    }

    private static HttpRequestMessage BuildExchangeRequest(Obtained got, Pkce pkce)
    {
        var payload = JsonSerializer.Serialize(new Dictionary<string, string>
        {
            ["grant_type"] = "authorization_code",
            ["code"] = got.Code,
            ["code_verifier"] = pkce.Verifier,
            ["client_id"] = ClaudeOAuth.ClientId,
            ["redirect_uri"] = got.RedirectUri,
            ["state"] = pkce.State,
        });
        var request = new HttpRequestMessage(HttpMethod.Post, ClaudeOAuth.TokenEndpoint)
        {
            Content = new StringContent(payload, Encoding.UTF8, "application/json"),
        };
        request.Headers.TryAddWithoutValidation("User-Agent", "anthropic");
        return request;
    }

    private (WidgetTokens Tokens, string? Account)? ParseTokens(string body)
    {
        try
        {
            using var doc = JsonDocument.Parse(body);
            var root = doc.RootElement;
            if (root.ValueKind != JsonValueKind.Object)
                return null;

            if (!TryString(root, "access_token", out var access)
                || !TryString(root, "refresh_token", out var refresh)
                || !root.TryGetProperty("expires_in", out var expiresEl)
                || expiresEl.ValueKind != JsonValueKind.Number
                || !expiresEl.TryGetInt64(out var expiresIn))
                return null;

            var scope = TryString(root, "scope", out var granted) ? granted : ClaudeOAuth.Scope;

            string? account = null;
            if (root.TryGetProperty("account", out var acc)
                && acc.ValueKind == JsonValueKind.Object
                && TryString(acc, "email_address", out var email))
                account = email;

            // The token response carries no refresh-token expiry; leave it unknown (epoch 0) — nothing gates on it.
            var tokens = new WidgetTokens(
                access, refresh, _time.GetUtcNow().AddSeconds(expiresIn),
                DateTimeOffset.FromUnixTimeMilliseconds(0), scope);
            return (tokens, account);
        }
        catch (JsonException)
        {
            return null;
        }
    }

    private static bool TryString(JsonElement obj, string name, out string value)
    {
        if (obj.TryGetProperty(name, out var el)
            && el.ValueKind == JsonValueKind.String
            && el.GetString() is { Length: > 0 } s)
        {
            value = s;
            return true;
        }
        value = string.Empty;
        return false;
    }

    private readonly record struct Obtained(string Code, string? State, string RedirectUri);
}
