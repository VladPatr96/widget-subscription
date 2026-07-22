using System.Net;
using System.Text.Json;
using Microsoft.Extensions.Time.Testing;
using WidgetSubscription.Providers.ClaudeCode;
using Xunit;

namespace WidgetSubscription.Tests;

/// <summary>
/// The own-login credential source (#19): lazy just-in-time refresh with skew, single-flight
/// rotation, and the transient/terminal split expressed through the store (tokens kept vs cleared).
/// The token endpoint is driven through a fake <see cref="HttpMessageHandler"/> under a
/// <see cref="FakeTimeProvider"/>; the store is an in-memory fake. Behavior only.
/// </summary>
public sealed class OwnLoginCredentialSourceTests
{
    private static readonly DateTimeOffset Now = new(2026, 7, 21, 12, 0, 0, TimeSpan.Zero);

    [Fact]
    public async Task No_stored_tokens_yields_null_without_http()
    {
        var handler = new StubHandler((_, _) => throw new InvalidOperationException("HTTP must not be called."));
        var store = new InMemoryTokenStore(null);
        var source = Build(store, handler);

        Assert.Null(await source.GetAsync(CancellationToken.None));
        Assert.Equal(0, handler.Calls);
    }

    [Fact]
    public async Task Fresh_token_is_served_from_cache_without_http()
    {
        var store = new InMemoryTokenStore(Stored(Now.AddHours(2), access: "good-access"));
        var handler = new StubHandler((_, _) => throw new InvalidOperationException("HTTP must not be called."));
        var source = Build(store, handler);

        var token = await source.GetAsync(CancellationToken.None);

        Assert.Equal("good-access", token!.Value);
        Assert.Equal(Now.AddHours(2), token.ExpiresAt);
        Assert.Equal(0, handler.Calls);
    }

    [Fact]
    public async Task Token_within_skew_is_refreshed_and_rotation_persisted()
    {
        // Expires in 3 min, inside the default 5-min skew ⇒ refresh.
        var store = new InMemoryTokenStore(Stored(Now.AddMinutes(3), access: "old-access", refresh: "old-refresh"));
        var handler = new StubHandler((_, _) =>
            Respond(HttpStatusCode.OK, TokenJson("new-access", "new-refresh", expiresIn: 28800)));
        var source = Build(store, handler);

        var token = await source.GetAsync(CancellationToken.None);

        Assert.Equal("new-access", token!.Value);
        Assert.Equal(Now.AddSeconds(28800), token.ExpiresAt);   // now + expires_in
        var persisted = store.Load();
        Assert.Equal("new-access", persisted!.AccessToken);
        Assert.Equal("new-refresh", persisted.RefreshToken);    // rotated: old refresh replaced
        Assert.Equal(1, store.SaveCount);
        Assert.Equal(1, handler.Calls);
    }

    [Fact]
    public async Task Refresh_request_is_a_json_refresh_grant_to_the_token_endpoint()
    {
        var store = new InMemoryTokenStore(Stored(Now, refresh: "the-refresh-token"));
        var handler = new StubHandler((_, _) => Respond(HttpStatusCode.OK, TokenJson("a", "r")));
        var source = Build(store, handler);

        await source.GetAsync(CancellationToken.None);

        Assert.Equal(HttpMethod.Post, handler.LastRequest!.Method);
        Assert.Equal("https://console.anthropic.com/v1/oauth/token", handler.LastRequest.RequestUri!.ToString());
        Assert.Equal("application/json", handler.LastRequest.Content!.Headers.ContentType!.MediaType);
        using var body = JsonDocument.Parse(handler.LastBody!);
        Assert.Equal("refresh_token", body.RootElement.GetProperty("grant_type").GetString());
        Assert.Equal("the-refresh-token", body.RootElement.GetProperty("refresh_token").GetString());
        Assert.Equal("9d1c250a-e61b-44d9-88ed-5944d1962f5e", body.RootElement.GetProperty("client_id").GetString());
    }

    [Theory]
    [InlineData(HttpStatusCode.BadRequest)]   // invalid_grant
    [InlineData(HttpStatusCode.Unauthorized)]
    [InlineData(HttpStatusCode.Forbidden)]
    public async Task Terminal_refresh_failure_clears_the_store(HttpStatusCode status)
    {
        var store = new InMemoryTokenStore(Stored(Now));
        var handler = new StubHandler((_, _) => Respond(status, """{ "error": "invalid_grant" }"""));
        var source = Build(store, handler);

        var token = await source.GetAsync(CancellationToken.None);

        Assert.Null(token);
        Assert.Null(store.Load());          // grant is dead
        Assert.Equal(1, store.ClearCount);
    }

    [Fact]
    public async Task Transient_server_error_keeps_the_store()
    {
        var store = new InMemoryTokenStore(Stored(Now, access: "old-access"));
        var handler = new StubHandler((_, _) => Respond(HttpStatusCode.InternalServerError, "boom"));
        var source = Build(store, handler);

        var token = await source.GetAsync(CancellationToken.None);

        Assert.Null(token);
        Assert.NotNull(store.Load());       // session preserved for retry
        Assert.Equal(0, store.ClearCount);
        Assert.Equal(0, store.SaveCount);
    }

    [Fact]
    public async Task Transient_network_error_keeps_the_store()
    {
        var store = new InMemoryTokenStore(Stored(Now));
        var handler = new StubHandler((_, _) => throw new HttpRequestException("offline"));
        var source = Build(store, handler);

        var token = await source.GetAsync(CancellationToken.None);

        Assert.Null(token);
        Assert.NotNull(store.Load());
        Assert.Equal(0, store.ClearCount);
    }

    [Fact]
    public async Task Concurrent_expired_reads_refresh_once()
    {
        var store = new InMemoryTokenStore(Stored(Now.AddMinutes(1), access: "old", refresh: "old-refresh"));
        var release = new TaskCompletionSource();
        var handler = new StubHandler(async (_, _) =>
        {
            await release.Task;
            return await Respond(HttpStatusCode.OK, TokenJson("new", "new-refresh"));
        });
        var source = Build(store, handler);

        var first = source.GetAsync(CancellationToken.None);
        var second = source.GetAsync(CancellationToken.None);
        release.SetResult();
        var r1 = await first;
        var r2 = await second;

        Assert.Equal(1, handler.Calls);     // single-flight collapsed the second refresh
        Assert.Equal(1, store.SaveCount);
        Assert.Equal("new", r1!.Value);
        Assert.Equal("new", r2!.Value);
    }

    [Fact]
    public async Task Invalidate_forces_a_refresh_of_an_unexpired_token()
    {
        var store = new InMemoryTokenStore(Stored(Now.AddHours(2), access: "revoked-but-unexpired", refresh: "r"));
        var handler = new StubHandler((_, _) => Respond(HttpStatusCode.OK, TokenJson("fresh", "r2")));
        var source = Build(store, handler);

        // Without a hint the far-from-expiry token is served from cache.
        Assert.Equal("revoked-but-unexpired", (await source.GetAsync(CancellationToken.None))!.Value);
        Assert.Equal(0, handler.Calls);

        // A 401 marks it rejected; the next read must refresh despite the distant expiry.
        source.Invalidate();
        var token = await source.GetAsync(CancellationToken.None);

        Assert.Equal("fresh", token!.Value);
        Assert.Equal(1, handler.Calls);

        // Flag cleared: a subsequent read is served from cache again.
        Assert.Equal("fresh", (await source.GetAsync(CancellationToken.None))!.Value);
        Assert.Equal(1, handler.Calls);
    }

    // --- helpers ---------------------------------------------------------

    private static OwnLoginCredentialSource Build(IWidgetTokenStore store, StubHandler handler)
        => new(new HttpClient(handler), store, new FakeTimeProvider(Now));

    private static WidgetTokens Stored(
        DateTimeOffset expiresAt, string access = "access", string refresh = "refresh")
        => new(access, refresh, expiresAt, expiresAt.AddDays(30), "user:inference user:profile");

    private static string TokenJson(string access, string refresh, int expiresIn = 28800)
        => $$"""
        { "token_type": "Bearer", "access_token": "{{access}}", "refresh_token": "{{refresh}}",
          "expires_in": {{expiresIn}}, "scope": "user:inference user:profile" }
        """;

    private static Task<HttpResponseMessage> Respond(HttpStatusCode status, string body)
        => Task.FromResult(new HttpResponseMessage(status)
        {
            Content = new StringContent(body, System.Text.Encoding.UTF8, "application/json"),
        });

    private sealed class InMemoryTokenStore : IWidgetTokenStore
    {
        private WidgetTokens? _tokens;

        public InMemoryTokenStore(WidgetTokens? initial) => _tokens = initial;

        public int SaveCount { get; private set; }
        public int ClearCount { get; private set; }

        public WidgetTokens? Load() => _tokens;
        public void Save(WidgetTokens tokens) { _tokens = tokens; SaveCount++; }
        public void Clear() { _tokens = null; ClearCount++; }
    }

    private sealed class StubHandler : HttpMessageHandler
    {
        private readonly Func<HttpRequestMessage, CancellationToken, Task<HttpResponseMessage>> _respond;

        public StubHandler(Func<HttpRequestMessage, CancellationToken, Task<HttpResponseMessage>> respond)
            => _respond = respond;

        public int Calls { get; private set; }
        public HttpRequestMessage? LastRequest { get; private set; }
        public string? LastBody { get; private set; }

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
        {
            Calls++;
            LastRequest = request;
            if (request.Content is not null)
                LastBody = await request.Content.ReadAsStringAsync(ct);
            return await _respond(request, ct);
        }
    }
}
