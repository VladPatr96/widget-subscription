using System.Net;
using System.Text;
using System.Text.Json;

namespace WidgetSubscription.Providers.ClaudeCode;

/// <summary>
/// The own-login <see cref="ICredentialSource"/> (#19). Serves the widget's own OAuth access
/// token from an <see cref="IWidgetTokenStore"/>, refreshing it lazily just-in-time when it is
/// within <see cref="_skew"/> of expiry. Unlike the borrow source it <em>is</em> allowed to
/// refresh, because it owns a separate grant family (#14 §6). Concurrent refreshes are collapsed
/// with a single-flight <see cref="SemaphoreSlim"/> and the rotated triple is persisted atomically
/// (by the store) before the new access token is handed out (#19 §4–5).
/// </summary>
/// <remarks>
/// <see cref="GetAsync"/> returning <c>null</c> is disambiguated by the store, not by a richer
/// return type: tokens still present ⇒ a <em>transient</em> problem (session alive, retry next
/// poll with the engine's backoff); store cleared ⇒ the grant is <em>terminally</em> dead and a
/// fresh login is required (#19 §4.5). The Claude Code file is never touched here.
/// </remarks>
public sealed class OwnLoginCredentialSource : ICredentialSource, ICredentialInvalidation
{
    private readonly HttpClient _http;
    private readonly IWidgetTokenStore _store;
    private readonly TimeProvider _time;
    private readonly TimeSpan _skew;
    private readonly SemaphoreSlim _gate = new(1, 1);
    private volatile bool _forceRefresh;

    public OwnLoginCredentialSource(
        HttpClient http, IWidgetTokenStore store, TimeProvider? time = null, TimeSpan? skew = null)
    {
        _http = http;
        _store = store;
        _time = time ?? TimeProvider.System;
        _skew = skew ?? TimeSpan.FromMinutes(5);
    }

    public async Task<AccessToken?> GetAsync(CancellationToken ct)
    {
        var tokens = _store.Load();
        if (tokens is null)
        {
            _forceRefresh = false; // nothing cached to invalidate (dead grant or signed out)
            return null;
        }
        if (!ShouldRefresh(tokens))
            return new AccessToken(tokens.AccessToken, tokens.ExpiresAt);

        await _gate.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            // Re-read under the lock: a racing caller may have already rotated the token.
            tokens = _store.Load();
            if (tokens is null)
                return null;
            if (!ShouldRefresh(tokens))
                return new AccessToken(tokens.AccessToken, tokens.ExpiresAt);
            return await RefreshAsync(tokens, ct).ConfigureAwait(false);
        }
        finally
        {
            _gate.Release();
        }
    }

    // The next Get must refresh if the token is near expiry, or if a 401 marked it rejected.
    private bool ShouldRefresh(WidgetTokens tokens) => _forceRefresh || NeedsRefresh(tokens);

    private bool NeedsRefresh(WidgetTokens tokens) => _time.GetUtcNow() + _skew >= tokens.ExpiresAt;

    /// <summary>A 401 rejected the last token: force a refresh on the next <see cref="GetAsync"/>.</summary>
    public void Invalidate() => _forceRefresh = true;

    private async Task<AccessToken?> RefreshAsync(WidgetTokens current, CancellationToken ct)
    {
        HttpResponseMessage response;
        try
        {
            response = await _http.SendAsync(BuildRefreshRequest(current.RefreshToken), ct).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            throw;
        }
        catch (OperationCanceledException)
        {
            return null; // timeout ⇒ transient: keep tokens, retry next poll
        }
        catch (HttpRequestException)
        {
            return null; // network ⇒ transient: keep tokens
        }

        using (response)
        {
            if (IsTerminal(response.StatusCode))
            {
                // invalid_grant / revoked / reuse-detected ⇒ the grant is dead.
                _store.Clear();
                _forceRefresh = false;
                return null;
            }

            if (!response.IsSuccessStatusCode)
                return null; // 5xx / 429 ⇒ transient: keep tokens

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
                return null;
            }

            var refreshed = Parse(body, current);
            if (refreshed is null)
                return null; // malformed success ⇒ transient: keep tokens

            _store.Save(refreshed); // persist rotated triple before the new access token is used
            _forceRefresh = false;
            return new AccessToken(refreshed.AccessToken, refreshed.ExpiresAt);
        }
    }

    private static HttpRequestMessage BuildRefreshRequest(string refreshToken)
    {
        var payload = JsonSerializer.Serialize(new Dictionary<string, string>
        {
            ["grant_type"] = "refresh_token",
            ["refresh_token"] = refreshToken,
            ["client_id"] = ClaudeOAuth.ClientId,
        });
        var request = new HttpRequestMessage(HttpMethod.Post, ClaudeOAuth.TokenEndpoint)
        {
            Content = new StringContent(payload, Encoding.UTF8, "application/json"),
        };
        request.Headers.TryAddWithoutValidation("User-Agent", "anthropic");
        return request;
    }

    // A used/invalid refresh token or a revoked grant is terminal; retrying cannot recover it.
    private static bool IsTerminal(HttpStatusCode status)
        => status is HttpStatusCode.BadRequest or HttpStatusCode.Unauthorized or HttpStatusCode.Forbidden;

    private WidgetTokens? Parse(string body, WidgetTokens current)
    {
        try
        {
            using var doc = JsonDocument.Parse(body);
            var root = doc.RootElement;
            if (root.ValueKind != JsonValueKind.Object)
                return null;

            if (!root.TryGetProperty("access_token", out var accessEl)
                || accessEl.ValueKind != JsonValueKind.String
                || accessEl.GetString() is not { Length: > 0 } access)
                return null;

            if (!root.TryGetProperty("expires_in", out var expiresEl)
                || expiresEl.ValueKind != JsonValueKind.Number
                || !expiresEl.TryGetInt64(out var expiresIn))
                return null;

            // Rotation always returns a new refresh token; fall back to the current one only if absent.
            var refresh = root.TryGetProperty("refresh_token", out var refreshEl)
                && refreshEl.ValueKind == JsonValueKind.String
                && refreshEl.GetString() is { Length: > 0 } rotated
                ? rotated
                : current.RefreshToken;

            var scope = root.TryGetProperty("scope", out var scopeEl)
                && scopeEl.ValueKind == JsonValueKind.String
                && scopeEl.GetString() is { Length: > 0 } granted
                ? granted
                : current.Scope;

            return new WidgetTokens(
                access,
                refresh,
                _time.GetUtcNow().AddSeconds(expiresIn),
                current.RefreshTokenExpiresAt,
                scope);
        }
        catch (JsonException)
        {
            return null;
        }
    }
}
