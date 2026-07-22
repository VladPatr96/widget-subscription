using System.Net;
using System.Text;
using Microsoft.Extensions.Time.Testing;
using WidgetSubscription.Core;
using WidgetSubscription.Providers.ClaudeCode;
using Xunit;

namespace WidgetSubscription.Tests;

/// <summary>
/// The composite credential source (#17 §1, §4): per-call mode resolution (Auto/Borrow/Own) and the
/// invalidation forward that keeps the adapter's 401 → own-refresh path alive behind the composite
/// (§4.3). The last test wires the real adapter + composite + own-login source to prove that chain.
/// </summary>
public sealed class SelectingCredentialSourceTests
{
    private static readonly DateTimeOffset Now = new(2026, 7, 21, 12, 0, 0, TimeSpan.Zero);

    [Fact]
    public async Task Auto_uses_borrow_when_it_yields_a_token()
    {
        var borrow = new FakeSource(new AccessToken("borrowed", null));
        var own = new FakeSource(new AccessToken("own", null));
        var source = new SelectingCredentialSource(borrow, own, new FakeModeStore(CredentialMode.Auto));

        var token = await source.GetAsync(CancellationToken.None);

        Assert.Equal("borrowed", token!.Value);
        Assert.Equal(0, own.Calls);
    }

    [Fact]
    public async Task Auto_falls_back_to_own_when_borrow_is_absent()
    {
        var borrow = new FakeSource(null);
        var own = new FakeSource(new AccessToken("own", null));
        var source = new SelectingCredentialSource(borrow, own, new FakeModeStore(CredentialMode.Auto));

        var token = await source.GetAsync(CancellationToken.None);

        Assert.Equal("own", token!.Value);
        Assert.Equal(1, borrow.Calls);
    }

    [Fact]
    public async Task Own_mode_uses_own_and_never_touches_borrow()
    {
        var borrow = new FakeSource(new AccessToken("borrowed", null));
        var own = new FakeSource(new AccessToken("own", null));
        var source = new SelectingCredentialSource(borrow, own, new FakeModeStore(CredentialMode.Own));

        var token = await source.GetAsync(CancellationToken.None);

        Assert.Equal("own", token!.Value);
        Assert.Equal(0, borrow.Calls);
    }

    [Fact]
    public async Task Borrow_mode_uses_borrow_even_when_it_yields_nothing()
    {
        var borrow = new FakeSource(null);
        var own = new FakeSource(new AccessToken("own", null));
        var source = new SelectingCredentialSource(borrow, own, new FakeModeStore(CredentialMode.Borrow));

        var token = await source.GetAsync(CancellationToken.None);

        Assert.Null(token);              // stays borrow (degrades), does not force own
        Assert.Equal(0, own.Calls);
    }

    [Fact]
    public async Task Invalidate_forwards_to_the_active_own_source()
    {
        var own = new InvalidatingFakeSource(new AccessToken("own", null));
        var source = new SelectingCredentialSource(new FakeSource(null), own, new FakeModeStore(CredentialMode.Own));

        await source.GetAsync(CancellationToken.None);   // resolves active = own
        source.Invalidate();

        Assert.Equal(1, own.Invalidations);
    }

    [Fact]
    public async Task Invalidate_forwards_to_the_active_borrow_source()
    {
        var borrow = new InvalidatingFakeSource(new AccessToken("borrowed", null));
        var source = new SelectingCredentialSource(borrow, new FakeSource(null), new FakeModeStore(CredentialMode.Borrow));

        await source.GetAsync(CancellationToken.None);
        source.Invalidate();

        Assert.Equal(1, borrow.Invalidations);
    }

    [Fact]
    public void Invalidate_before_any_get_is_a_no_op()
    {
        var own = new InvalidatingFakeSource(new AccessToken("own", null));
        var source = new SelectingCredentialSource(new FakeSource(null), own, new FakeModeStore(CredentialMode.Own));

        source.Invalidate();   // no active source yet — must not throw

        Assert.Equal(0, own.Invalidations);
    }

    [Fact]
    public async Task Adapter_401_through_the_composite_forces_the_own_source_to_refresh()
    {
        // own-login grant: an unexpired-but-server-rejected access token; refresh mints "fresh".
        var store = new InMemoryStore(new WidgetTokens("stale", "r", Now.AddHours(2), Now.AddDays(30), "s"));
        var tokenEndpoint = new TokenHandler(
            """{ "access_token": "fresh", "refresh_token": "r2", "expires_in": 28800, "scope": "s" }""");
        var own = new OwnLoginCredentialSource(new HttpClient(tokenEndpoint), store, new FakeTimeProvider(Now));

        var composite = new SelectingCredentialSource(
            borrow: new FakeSource(null), own: own, mode: new FakeModeStore(CredentialMode.Auto));

        var usage = new UsageHandler(SampleUsage);
        var adapter = new ClaudeCodeAdapter(new HttpClient(usage), composite, new FakeTimeProvider(Now));

        var result = await adapter.FetchAsync(CancellationToken.None);

        Assert.IsType<FetchResult.Success>(result);
        Assert.Equal(1, tokenEndpoint.Calls);   // the 401 forced exactly one refresh
        Assert.Equal(2, usage.Calls);            // 401 with "stale", retry with "fresh"
        Assert.Equal("fresh", store.Load()!.AccessToken);
    }

    // --- helpers ---------------------------------------------------------

    private const string SampleUsage = """
    { "limits": [
      { "kind": "session",       "percent": 10, "resets_at": "2026-07-21T14:00:00+00:00", "scope": null, "is_active": true },
      { "kind": "weekly_all",    "percent": 5,  "resets_at": "2026-07-28T00:00:00+00:00", "scope": null, "is_active": false },
      { "kind": "weekly_scoped", "percent": 5,  "resets_at": "2026-07-28T00:00:00+00:00",
        "scope": { "model": { "display_name": "Fable" } }, "is_active": false } ] }
    """;

    private sealed class FakeSource : ICredentialSource
    {
        private readonly AccessToken? _token;
        public FakeSource(AccessToken? token) => _token = token;
        public int Calls { get; private set; }
        public Task<AccessToken?> GetAsync(CancellationToken ct)
        {
            Calls++;
            return Task.FromResult(_token);
        }
    }

    private sealed class InvalidatingFakeSource : ICredentialSource, ICredentialInvalidation
    {
        private readonly AccessToken? _token;
        public InvalidatingFakeSource(AccessToken? token) => _token = token;
        public int Invalidations { get; private set; }
        public Task<AccessToken?> GetAsync(CancellationToken ct) => Task.FromResult(_token);
        public void Invalidate() => Invalidations++;
    }

    private sealed class FakeModeStore : ICredentialModeStore
    {
        private CredentialMode _mode;
        public FakeModeStore(CredentialMode mode) => _mode = mode;
        public CredentialMode Get() => _mode;
        public void Set(CredentialMode mode) => _mode = mode;
    }

    private sealed class InMemoryStore : IWidgetTokenStore
    {
        private WidgetTokens? _tokens;
        public InMemoryStore(WidgetTokens? initial) => _tokens = initial;
        public WidgetTokens? Load() => _tokens;
        public void Save(WidgetTokens tokens) => _tokens = tokens;
        public void Clear() => _tokens = null;
    }

    private sealed class TokenHandler : HttpMessageHandler
    {
        private readonly string _body;
        public TokenHandler(string body) => _body = body;
        public int Calls { get; private set; }
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
        {
            Calls++;
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent(_body, Encoding.UTF8, "application/json"),
            });
        }
    }

    private sealed class UsageHandler : HttpMessageHandler
    {
        private readonly string _okBody;
        public UsageHandler(string okBody) => _okBody = okBody;
        public int Calls { get; private set; }
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
        {
            Calls++;
            var bearer = request.Headers.Authorization?.Parameter;
            return Task.FromResult(bearer == "fresh"
                ? new HttpResponseMessage(HttpStatusCode.OK)
                {
                    Content = new StringContent(_okBody, Encoding.UTF8, "application/json"),
                }
                : new HttpResponseMessage(HttpStatusCode.Unauthorized));
        }
    }
}
