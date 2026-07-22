using System.Net;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Microsoft.Extensions.Time.Testing;
using WidgetSubscription.Providers.ClaudeCode;
using Xunit;

namespace WidgetSubscription.Tests;

/// <summary>
/// The interactive own-login orchestration (#18): loopback capture with a hosted-paste fallback,
/// state validation, code exchange, and persistence — all through injected fakes for the browser,
/// loopback, and paste seams plus a fake token endpoint. The live browser/loopback edges are not
/// exercised here (see <see cref="HttpLoopbackListenerTests"/> for the real listener).
/// </summary>
public sealed class WidgetLoginTests
{
    private static readonly DateTimeOffset Now = new(2026, 7, 21, 12, 0, 0, TimeSpan.Zero);

    [Fact]
    public async Task Loopback_success_persists_tokens_and_returns_account()
    {
        var store = new InMemoryStore();
        var browser = new FakeBrowser();
        var listener = new FakeLoopbackListener(_ =>
            Task.FromResult(new LoopbackCallback("auth-code", StateOf(browser), null)));
        var codeEntry = new FakeCodeEntry(_ => Task.FromResult<string?>(null));
        var handler = new ExchangeHandler(HttpStatusCode.OK, TokenJson(access: "A", refresh: "R", email: "me@x.io"));
        var login = Build(store, browser, new FakeLoopbackFactory(listener), codeEntry, handler);

        var result = await login.LoginAsync(CancellationToken.None);

        var success = Assert.IsType<LoginResult.Success>(result);
        Assert.Equal("me@x.io", success.Account);
        Assert.Equal("A", store.Load()!.AccessToken);
        Assert.Equal("R", store.Load()!.RefreshToken);
        Assert.Equal(Now.AddSeconds(28800), store.Load()!.ExpiresAt);
        Assert.Equal(0, codeEntry.Calls);            // no paste needed
        Assert.Single(browser.Opened);               // one authorize URL
    }

    [Fact]
    public async Task Authorize_url_carries_pkce_scope_state_and_loopback_redirect()
    {
        var browser = new FakeBrowser();
        var listener = new FakeLoopbackListener(_ =>
            Task.FromResult(new LoopbackCallback("c", StateOf(browser), null)));
        var login = Build(new InMemoryStore(), browser, new FakeLoopbackFactory(listener),
            new FakeCodeEntry(_ => Task.FromResult<string?>(null)),
            new ExchangeHandler(HttpStatusCode.OK, TokenJson()));

        await login.LoginAsync(CancellationToken.None);

        var url = browser.Opened.Single();
        Assert.StartsWith("https://claude.ai/oauth/authorize?", url);
        Assert.Equal("9d1c250a-e61b-44d9-88ed-5944d1962f5e", Param(url, "client_id"));
        Assert.Equal("code", Param(url, "response_type"));
        Assert.Equal("user:inference user:profile", Param(url, "scope"));
        Assert.Equal("S256", Param(url, "code_challenge_method"));
        Assert.Equal("http://localhost:12345/callback", Param(url, "redirect_uri"));
        Assert.Null(Param(url, "code"));             // loopback path: no code=true

        // PKCE relationship: challenge = base64url(SHA-256(state)), since state == verifier.
        Assert.Equal(Base64UrlSha256(Param(url, "state")!), Param(url, "code_challenge"));
    }

    [Fact]
    public async Task Exchange_is_an_authorization_code_grant_with_verifier_and_redirect()
    {
        var browser = new FakeBrowser();
        var listener = new FakeLoopbackListener(_ =>
            Task.FromResult(new LoopbackCallback("the-code", StateOf(browser), null)));
        var handler = new ExchangeHandler(HttpStatusCode.OK, TokenJson());
        var login = Build(new InMemoryStore(), browser, new FakeLoopbackFactory(listener),
            new FakeCodeEntry(_ => Task.FromResult<string?>(null)), handler);

        await login.LoginAsync(CancellationToken.None);

        Assert.Equal(HttpMethod.Post, handler.LastRequest!.Method);
        Assert.Equal("https://console.anthropic.com/v1/oauth/token", handler.LastRequest.RequestUri!.ToString());
        using var body = JsonDocument.Parse(handler.LastBody!);
        var root = body.RootElement;
        Assert.Equal("authorization_code", root.GetProperty("grant_type").GetString());
        Assert.Equal("the-code", root.GetProperty("code").GetString());
        Assert.Equal("9d1c250a-e61b-44d9-88ed-5944d1962f5e", root.GetProperty("client_id").GetString());
        Assert.Equal("http://localhost:12345/callback", root.GetProperty("redirect_uri").GetString());
        // verifier == state, so it must equal the state in the authorize URL.
        Assert.Equal(Param(browser.Opened.Single(), "state"), root.GetProperty("code_verifier").GetString());
    }

    [Fact]
    public async Task Port_busy_falls_back_to_paste_with_hosted_redirect()
    {
        var store = new InMemoryStore();
        var browser = new FakeBrowser();
        var codeEntry = new FakeCodeEntry(_ => Task.FromResult<string?>("pasted-code"));
        var handler = new ExchangeHandler(HttpStatusCode.OK, TokenJson());
        var login = Build(store, browser, new FakeLoopbackFactory(null), codeEntry, handler);

        var result = await login.LoginAsync(CancellationToken.None);

        Assert.IsType<LoginResult.Success>(result);
        Assert.Equal(1, codeEntry.Calls);
        var url = browser.Opened.Single();
        Assert.Equal("https://console.anthropic.com/oauth/code/callback", Param(url, "redirect_uri"));
        Assert.Equal("true", Param(url, "code"));    // hosted-paste flag
        // Exchange used the pasted code and the hosted redirect.
        using var body = JsonDocument.Parse(handler.LastBody!);
        Assert.Equal("pasted-code", body.RootElement.GetProperty("code").GetString());
        Assert.Equal("https://console.anthropic.com/oauth/code/callback",
            body.RootElement.GetProperty("redirect_uri").GetString());
    }

    [Fact]
    public async Task Loopback_error_falls_back_to_paste()
    {
        var browser = new FakeBrowser();
        var listener = new FakeLoopbackListener(_ =>
            Task.FromResult(new LoopbackCallback(null, null, "access_denied")));
        var codeEntry = new FakeCodeEntry(_ => Task.FromResult<string?>("pasted"));
        var login = Build(new InMemoryStore(), browser, new FakeLoopbackFactory(listener), codeEntry,
            new ExchangeHandler(HttpStatusCode.OK, TokenJson()));

        var result = await login.LoginAsync(CancellationToken.None);

        Assert.IsType<LoginResult.Success>(result);
        Assert.Equal(1, codeEntry.Calls);
        Assert.Equal(2, browser.Opened.Count);       // loopback URL, then hosted URL
    }

    [Fact]
    public async Task Loopback_timeout_falls_back_to_paste()
    {
        var time = new FakeTimeProvider(Now);
        var browser = new FakeBrowser();
        var entered = new TaskCompletionSource();
        var listener = new FakeLoopbackListener(async ct =>
        {
            entered.TrySetResult();
            await Task.Delay(Timeout.Infinite, ct);
            throw new InvalidOperationException("unreachable");
        });
        var codeEntry = new FakeCodeEntry(_ => Task.FromResult<string?>("pasted"));
        var login = Build(new InMemoryStore(), browser, new FakeLoopbackFactory(listener), codeEntry,
            new ExchangeHandler(HttpStatusCode.OK, TokenJson()), time, TimeSpan.FromMinutes(3));

        var task = login.LoginAsync(CancellationToken.None);
        await entered.Task;                           // inside WaitForCallbackAsync; timeout armed
        time.Advance(TimeSpan.FromMinutes(3) + TimeSpan.FromSeconds(1));
        var result = await task;

        Assert.IsType<LoginResult.Success>(result);
        Assert.Equal(1, codeEntry.Calls);
    }

    [Fact]
    public async Task State_mismatch_fails_without_persisting()
    {
        var store = new InMemoryStore();
        var listener = new FakeLoopbackListener(_ =>
            Task.FromResult(new LoopbackCallback("code", "not-the-real-state", null)));
        var handler = new ExchangeHandler(HttpStatusCode.OK, TokenJson());
        var login = Build(store, new FakeBrowser(), new FakeLoopbackFactory(listener),
            new FakeCodeEntry(_ => Task.FromResult<string?>(null)), handler);

        var result = await login.LoginAsync(CancellationToken.None);

        Assert.IsType<LoginResult.Failed>(result);
        Assert.Null(store.Load());
        Assert.Equal(0, handler.Calls);              // no exchange attempted
    }

    [Fact]
    public async Task Paste_cancel_returns_cancelled_without_persisting()
    {
        var store = new InMemoryStore();
        var login = Build(store, new FakeBrowser(), new FakeLoopbackFactory(null),
            new FakeCodeEntry(_ => Task.FromResult<string?>(null)),   // user cancels
            new ExchangeHandler(HttpStatusCode.OK, TokenJson()));

        var result = await login.LoginAsync(CancellationToken.None);

        Assert.IsType<LoginResult.Cancelled>(result);
        Assert.Null(store.Load());
    }

    [Fact]
    public async Task Paste_code_hash_state_is_parsed_and_validated()
    {
        var store = new InMemoryStore();
        var browser = new FakeBrowser();
        // Paste the hosted "CODE#STATE" form, echoing the real state so it validates.
        var codeEntry = new FakeCodeEntry(_ => Task.FromResult<string?>($"hosted-code#{StateOf(browser)}"));
        var handler = new ExchangeHandler(HttpStatusCode.OK, TokenJson());
        var login = Build(store, browser, new FakeLoopbackFactory(null), codeEntry, handler);

        var result = await login.LoginAsync(CancellationToken.None);

        Assert.IsType<LoginResult.Success>(result);
        using var body = JsonDocument.Parse(handler.LastBody!);
        Assert.Equal("hosted-code", body.RootElement.GetProperty("code").GetString());
    }

    [Fact]
    public async Task Exchange_http_failure_returns_failed_without_persisting()
    {
        var store = new InMemoryStore();
        var browser = new FakeBrowser();
        var listener = new FakeLoopbackListener(_ =>
            Task.FromResult(new LoopbackCallback("code", StateOf(browser), null)));
        var handler = new ExchangeHandler(HttpStatusCode.BadRequest, """{ "error": "invalid_grant" }""");
        var login = Build(store, browser, new FakeLoopbackFactory(listener),
            new FakeCodeEntry(_ => Task.FromResult<string?>(null)), handler);

        var result = await login.LoginAsync(CancellationToken.None);

        Assert.IsType<LoginResult.Failed>(result);
        Assert.Null(store.Load());
    }

    [Fact]
    public void SignOut_clears_the_store()
    {
        var store = new InMemoryStore();
        store.Save(new WidgetTokens("a", "r", Now, Now, "s"));
        var login = Build(store, new FakeBrowser(), new FakeLoopbackFactory(null),
            new FakeCodeEntry(_ => Task.FromResult<string?>(null)),
            new ExchangeHandler(HttpStatusCode.OK, TokenJson()));

        login.SignOut();

        Assert.Null(store.Load());
    }

    [Fact]
    public async Task Manual_entry_request_abandons_loopback_and_uses_paste()
    {
        var browser = new FakeBrowser();
        var listener = new FakeLoopbackListener(async ct =>
        {
            await Task.Delay(Timeout.Infinite, ct);   // never returns until the wait is cancelled
            throw new InvalidOperationException("unreachable");
        });
        var codeEntry = new ManualCodeEntry(requestNow: true, pasted: "manual-code");
        var handler = new ExchangeHandler(HttpStatusCode.OK, TokenJson());
        var login = Build(new InMemoryStore(), browser, new FakeLoopbackFactory(listener), codeEntry, handler);

        var result = await login.LoginAsync(CancellationToken.None);

        Assert.IsType<LoginResult.Success>(result);
        Assert.Equal(1, codeEntry.Calls);            // fell through to paste
        Assert.Equal(2, browser.Opened.Count);       // loopback URL, then hosted URL
        using var body = JsonDocument.Parse(handler.LastBody!);
        Assert.Equal("manual-code", body.RootElement.GetProperty("code").GetString());
    }

    // --- helpers ---------------------------------------------------------

    private static WidgetLogin Build(
        InMemoryStore store, FakeBrowser browser, FakeLoopbackFactory factory,
        ICodeEntry codeEntry, ExchangeHandler handler,
        TimeProvider? time = null, TimeSpan? timeout = null)
        => new(new HttpClient(handler), store, browser, factory, codeEntry, time ?? new FakeTimeProvider(Now), timeout);

    private static string StateOf(FakeBrowser browser) => Param(browser.LastUrl!, "state")!;

    private static string? Param(string url, string key)
    {
        var query = new Uri(url).Query.TrimStart('?');
        foreach (var pair in query.Split('&', StringSplitOptions.RemoveEmptyEntries))
        {
            var eq = pair.IndexOf('=');
            if (eq > 0 && Uri.UnescapeDataString(pair[..eq]) == key)
                return Uri.UnescapeDataString(pair[(eq + 1)..]);
        }
        return null;
    }

    private static string Base64UrlSha256(string value)
        => Convert.ToBase64String(SHA256.HashData(Encoding.ASCII.GetBytes(value)))
            .TrimEnd('=').Replace('+', '-').Replace('/', '_');

    private static string TokenJson(string access = "acc", string refresh = "ref", int expiresIn = 28800, string? email = null)
    {
        var account = email is null ? "" : $$""", "account": { "email_address": "{{email}}" }""";
        return $$"""
        { "token_type": "Bearer", "access_token": "{{access}}", "refresh_token": "{{refresh}}",
          "expires_in": {{expiresIn}}, "scope": "user:inference user:profile"{{account}} }
        """;
    }

    private sealed class FakeBrowser : IBrowserLauncher
    {
        public List<string> Opened { get; } = new();
        public string? LastUrl => Opened.Count > 0 ? Opened[^1] : null;
        public void Open(string url) => Opened.Add(url);
    }

    private sealed class FakeLoopbackFactory : ILoopbackListenerFactory
    {
        private readonly ILoopbackListener? _listener;
        public FakeLoopbackFactory(ILoopbackListener? listener) => _listener = listener;
        public ILoopbackListener? TryStart() => _listener;
    }

    private sealed class FakeLoopbackListener : ILoopbackListener
    {
        private readonly Func<CancellationToken, Task<LoopbackCallback>> _wait;
        public FakeLoopbackListener(Func<CancellationToken, Task<LoopbackCallback>> wait) => _wait = wait;
        public string RedirectUri => "http://localhost:12345/callback";
        public Task<LoopbackCallback> WaitForCallbackAsync(CancellationToken ct) => _wait(ct);
        public void Dispose() { }
    }

    private sealed class FakeCodeEntry : ICodeEntry
    {
        private readonly Func<CancellationToken, Task<string?>> _prompt;
        public FakeCodeEntry(Func<CancellationToken, Task<string?>> prompt) => _prompt = prompt;
        public int Calls { get; private set; }
        public Task<string?> PromptAsync(CancellationToken ct)
        {
            Calls++;
            return _prompt(ct);
        }
    }

    private sealed class ManualCodeEntry : ICodeEntry, IManualEntrySignal
    {
        private readonly string? _pasted;
        private readonly CancellationTokenSource _manual = new();
        public ManualCodeEntry(bool requestNow, string? pasted)
        {
            _pasted = pasted;
            if (requestNow)
                _manual.Cancel();
        }
        public int Calls { get; private set; }
        public CancellationToken ManualEntryRequested => _manual.Token;
        public Task<string?> PromptAsync(CancellationToken ct)
        {
            Calls++;
            return Task.FromResult(_pasted);
        }
    }

    private sealed class InMemoryStore : IWidgetTokenStore
    {
        private WidgetTokens? _tokens;
        public WidgetTokens? Load() => _tokens;
        public void Save(WidgetTokens tokens) => _tokens = tokens;
        public void Clear() => _tokens = null;
    }

    private sealed class ExchangeHandler : HttpMessageHandler
    {
        private readonly HttpStatusCode _status;
        private readonly string _body;
        public ExchangeHandler(HttpStatusCode status, string body) { _status = status; _body = body; }

        public int Calls { get; private set; }
        public HttpRequestMessage? LastRequest { get; private set; }
        public string? LastBody { get; private set; }

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
        {
            Calls++;
            LastRequest = request;
            if (request.Content is not null)
                LastBody = await request.Content.ReadAsStringAsync(ct);
            return new HttpResponseMessage(_status)
            {
                Content = new StringContent(_body, Encoding.UTF8, "application/json"),
            };
        }
    }
}
