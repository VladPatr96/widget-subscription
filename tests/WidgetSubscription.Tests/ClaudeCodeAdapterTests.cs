using System.Net;
using System.Net.Http.Headers;
using WidgetSubscription.Core;
using WidgetSubscription.Providers.ClaudeCode;
using Xunit;

namespace WidgetSubscription.Tests;

/// <summary>
/// Seam 2 (spec #8): the HTTP boundary of <see cref="ClaudeCodeAdapter"/>, driven through a
/// fake <see cref="HttpMessageHandler"/> and a fake <see cref="ICredentialSource"/>. The adapter
/// *is* the port implementation, so it cannot be reached through the Core seam — it is tested here.
/// Behavior only: given a response + credentials, which <see cref="FetchResult"/> comes out.
/// </summary>
public class ClaudeCodeAdapterTests
{
    // A real, secret-free endpoint payload (docs/research/ticket-2-data-source.md).
    private const string SampleBody = """
    {
      "five_hour": { "utilization": 15.0, "resets_at": "2026-07-17T14:59:59.625643+00:00" },
      "seven_day": { "utilization": 2.0,  "resets_at": "2026-07-24T06:59:59.625670+00:00" },
      "extra_usage": { "is_enabled": false },
      "limits": [
        { "kind": "session",       "group": "session", "percent": 15, "severity": "normal",
          "resets_at": "2026-07-17T14:59:59.625643+00:00", "scope": null, "is_active": true },
        { "kind": "weekly_all",    "group": "weekly",  "percent": 2,  "severity": "normal",
          "resets_at": "2026-07-24T06:59:59.625670+00:00", "scope": null, "is_active": false },
        { "kind": "weekly_scoped", "group": "weekly",  "percent": 2,  "severity": "normal",
          "resets_at": "2026-07-24T06:59:59.626085+00:00",
          "scope": { "model": { "id": null, "display_name": "Fable" }, "surface": null },
          "is_active": false }
      ]
    }
    """;

    [Fact]
    public void Info_is_available_without_a_fetch()
    {
        var adapter = Build(new FakeCredentials("t"), Responder.Never());

        Assert.Equal("claude-code", adapter.Info.Id);
        Assert.Equal("Claude Code", adapter.Info.DisplayName);
    }

    [Fact]
    public async Task Maps_the_three_limits_with_headroom_and_absolute_reset()
    {
        var handler = Responder.Always(() => Json(HttpStatusCode.OK, SampleBody));
        var adapter = Build(new FakeCredentials("secret-token"), handler);

        var result = await adapter.FetchAsync(CancellationToken.None);

        var snapshot = Assert.IsType<FetchResult.Success>(result).Snapshot;
        Assert.Equal(3, snapshot.Limits.Count);

        var session = snapshot.Limits.Single(l => l.Kind == LimitKind.Session);
        Assert.Equal(85, session.Headroom);              // 100 - 15
        Assert.True(session.IsActive);
        Assert.Equal(
            new DateTimeOffset(2026, 7, 17, 14, 59, 59, 625, TimeSpan.Zero).AddTicks(6430),
            session.ResetsAt);

        var weeklyAll = snapshot.Limits.Single(l => l.Kind == LimitKind.WeeklyAll);
        Assert.Equal(98, weeklyAll.Headroom);            // 100 - 2
        Assert.False(weeklyAll.IsActive);

        var fable = snapshot.Limits.Single(l => l.Kind == LimitKind.WeeklyScoped);
        Assert.Equal(98, fable.Headroom);
        Assert.Equal("Fable 5", fable.DisplayName);
    }

    [Fact]
    public async Task Sends_bearer_token_and_oauth_headers()
    {
        HttpRequestMessage? seen = null;
        var handler = Responder.Capturing(req => { seen = req; return Json(HttpStatusCode.OK, SampleBody); });
        var adapter = Build(new FakeCredentials("secret-token"), handler);

        await adapter.FetchAsync(CancellationToken.None);

        Assert.NotNull(seen);
        Assert.Equal(HttpMethod.Get, seen!.Method);
        Assert.Equal("https://api.anthropic.com/api/oauth/usage", seen.RequestUri!.ToString());
        Assert.Equal("Bearer", seen.Headers.Authorization!.Scheme);
        Assert.Equal("secret-token", seen.Headers.Authorization.Parameter);
        Assert.Equal("oauth-2025-04-20", Header(seen, "anthropic-beta"));
        Assert.Equal("2023-06-01", Header(seen, "anthropic-version"));
    }

    [Fact]
    public async Task No_credentials_yields_NoCredentials_without_calling_http()
    {
        var handler = Responder.Never();
        var adapter = Build(new FakeCredentials((string?)null), handler);

        var result = await adapter.FetchAsync(CancellationToken.None);

        var failure = Assert.IsType<FetchResult.Failure>(result);
        Assert.Equal(FetchErrorKind.NoCredentials, failure.Error.Kind);
        Assert.Equal(0, handler.Calls);
    }

    [Fact]
    public async Task Unauthorized_rereads_credentials_and_retries_once_then_succeeds()
    {
        var creds = new FakeCredentials("stale", "fresh");
        var handler = Responder.Sequence(
            () => new HttpResponseMessage(HttpStatusCode.Unauthorized),
            () => Json(HttpStatusCode.OK, SampleBody));
        var adapter = Build(creds, handler);

        var result = await adapter.FetchAsync(CancellationToken.None);

        Assert.IsType<FetchResult.Success>(result);
        Assert.Equal(2, handler.Calls);      // one 401, one retry
        Assert.Equal(2, creds.Calls);        // token re-read before the retry
    }

    [Fact]
    public async Task Unauthorized_invalidates_the_source_before_the_reread()
    {
        // A refreshable source (own-login) must be told its token was rejected so the reread
        // forces a refresh rather than returning the same token (spec §4.3).
        var creds = new InvalidatingCredentials("stale", "fresh");
        var handler = Responder.Sequence(
            () => new HttpResponseMessage(HttpStatusCode.Unauthorized),
            () => Json(HttpStatusCode.OK, SampleBody));
        var adapter = Build(creds, handler);

        var result = await adapter.FetchAsync(CancellationToken.None);

        Assert.IsType<FetchResult.Success>(result);
        Assert.Equal(1, creds.Invalidations);
        Assert.True(creds.InvalidatedBeforeReread);
    }

    [Fact]
    public async Task Unauthorized_after_retry_yields_Unauthorized()
    {
        var creds = new FakeCredentials("stale", "still-stale");
        var handler = Responder.Sequence(
            () => new HttpResponseMessage(HttpStatusCode.Unauthorized),
            () => new HttpResponseMessage(HttpStatusCode.Unauthorized));
        var adapter = Build(creds, handler);

        var result = await adapter.FetchAsync(CancellationToken.None);

        var failure = Assert.IsType<FetchResult.Failure>(result);
        Assert.Equal(FetchErrorKind.Unauthorized, failure.Error.Kind);
        Assert.Equal(2, handler.Calls);
    }

    [Fact]
    public async Task Transport_error_yields_SourceUnavailable()
    {
        var handler = Responder.Always(() => throw new HttpRequestException("connection refused"));
        var adapter = Build(new FakeCredentials("t"), handler);

        var result = await adapter.FetchAsync(CancellationToken.None);

        var failure = Assert.IsType<FetchResult.Failure>(result);
        Assert.Equal(FetchErrorKind.SourceUnavailable, failure.Error.Kind);
    }

    [Fact]
    public async Task Handler_timeout_yields_Timeout()
    {
        var handler = Responder.Always(() => throw new TaskCanceledException("timed out"));
        var adapter = Build(new FakeCredentials("t"), handler);

        var result = await adapter.FetchAsync(CancellationToken.None);

        var failure = Assert.IsType<FetchResult.Failure>(result);
        Assert.Equal(FetchErrorKind.Timeout, failure.Error.Kind);
    }

    [Fact]
    public async Task Caller_cancellation_propagates_and_is_not_swallowed()
    {
        using var cts = new CancellationTokenSource();
        cts.Cancel();
        var handler = Responder.Always(() => throw new OperationCanceledException(cts.Token));
        var adapter = Build(new FakeCredentials("t"), handler);

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => adapter.FetchAsync(cts.Token));
    }

    [Fact]
    public async Task Server_error_carries_RetryAfter()
    {
        var handler = Responder.Always(() =>
        {
            var response = new HttpResponseMessage(HttpStatusCode.TooManyRequests);
            response.Headers.RetryAfter = new RetryConditionHeaderValue(TimeSpan.FromSeconds(120));
            return response;
        });
        var adapter = Build(new FakeCredentials("t"), handler);

        var result = await adapter.FetchAsync(CancellationToken.None);

        var failure = Assert.IsType<FetchResult.Failure>(result);
        Assert.Equal(FetchErrorKind.SourceUnavailable, failure.Error.Kind);
        Assert.Equal(TimeSpan.FromSeconds(120), failure.Error.RetryAfter);
    }

    [Fact]
    public async Task Malformed_body_yields_Malformed()
    {
        var handler = Responder.Always(() => Json(HttpStatusCode.OK, "{ not json"));
        var adapter = Build(new FakeCredentials("t"), handler);

        var result = await adapter.FetchAsync(CancellationToken.None);

        Assert.Equal(FetchErrorKind.Malformed, Assert.IsType<FetchResult.Failure>(result).Error.Kind);
    }

    [Fact]
    public async Task Missing_a_required_limit_yields_Malformed()
    {
        const string onlySession = """
        { "limits": [ { "kind": "session", "percent": 10,
          "resets_at": "2026-07-17T14:59:59+00:00", "scope": null, "is_active": true } ] }
        """;
        var handler = Responder.Always(() => Json(HttpStatusCode.OK, onlySession));
        var adapter = Build(new FakeCredentials("t"), handler);

        var result = await adapter.FetchAsync(CancellationToken.None);

        Assert.Equal(FetchErrorKind.Malformed, Assert.IsType<FetchResult.Failure>(result).Error.Kind);
    }

    [Fact]
    public async Task Overage_percent_clamps_headroom_to_zero()
    {
        const string overage = """
        {
          "limits": [
            { "kind": "session",       "percent": 130,
              "resets_at": "2026-07-17T14:59:59+00:00", "scope": null, "is_active": true },
            { "kind": "weekly_all",    "percent": 2,
              "resets_at": "2026-07-24T06:59:59+00:00", "scope": null, "is_active": false },
            { "kind": "weekly_scoped", "percent": 2,
              "resets_at": "2026-07-24T06:59:59+00:00",
              "scope": { "model": { "display_name": "Fable" } }, "is_active": false }
          ]
        }
        """;
        var handler = Responder.Always(() => Json(HttpStatusCode.OK, overage));
        var adapter = Build(new FakeCredentials("t"), handler);

        var result = await adapter.FetchAsync(CancellationToken.None);

        var snapshot = Assert.IsType<FetchResult.Success>(result).Snapshot;
        Assert.Equal(0, snapshot.Limits.Single(l => l.Kind == LimitKind.Session).Headroom);
    }

    // --- helpers ---------------------------------------------------------

    private static ClaudeCodeAdapter Build(ICredentialSource credentials, Responder handler)
        => new(new HttpClient(handler), credentials);

    private static HttpResponseMessage Json(HttpStatusCode status, string body)
        => new(status) { Content = new StringContent(body, System.Text.Encoding.UTF8, "application/json") };

    private static string? Header(HttpRequestMessage request, string name)
        => request.Headers.TryGetValues(name, out var values) ? string.Join(",", values) : null;

    private sealed class FakeCredentials : ICredentialSource
    {
        private readonly Queue<AccessToken?> _tokens;
        private readonly AccessToken? _last;

        public FakeCredentials(params string?[] tokens)
        {
            _tokens = new Queue<AccessToken?>(
                tokens.Select(t => t is null ? null : new AccessToken(t, null)));
            _last = _tokens.Count > 0 ? _tokens.Last() : null;
        }

        public int Calls { get; private set; }

        public Task<AccessToken?> GetAsync(CancellationToken ct)
        {
            Calls++;
            var token = _tokens.Count > 0 ? _tokens.Dequeue() : _last;
            return Task.FromResult(token);
        }
    }

    private sealed class InvalidatingCredentials : ICredentialSource, ICredentialInvalidation
    {
        private readonly Queue<AccessToken?> _tokens;
        private readonly AccessToken? _last;

        public InvalidatingCredentials(params string?[] tokens)
        {
            _tokens = new Queue<AccessToken?>(
                tokens.Select(t => t is null ? null : new AccessToken(t, null)));
            _last = _tokens.Count > 0 ? _tokens.Last() : null;
        }

        public int Calls { get; private set; }
        public int Invalidations { get; private set; }
        public bool InvalidatedBeforeReread { get; private set; }

        public Task<AccessToken?> GetAsync(CancellationToken ct)
        {
            Calls++;
            if (Calls == 2)
                InvalidatedBeforeReread = Invalidations > 0;
            var token = _tokens.Count > 0 ? _tokens.Dequeue() : _last;
            return Task.FromResult(token);
        }

        public void Invalidate() => Invalidations++;
    }

    private sealed class Responder : HttpMessageHandler
    {
        private readonly Func<HttpRequestMessage, int, HttpResponseMessage> _respond;

        private Responder(Func<HttpRequestMessage, int, HttpResponseMessage> respond) => _respond = respond;

        public int Calls { get; private set; }

        public static Responder Never() => new((_, _) => throw new InvalidOperationException("HTTP must not be called."));

        public static Responder Always(Func<HttpResponseMessage> respond) => new((_, _) => respond());

        public static Responder Capturing(Func<HttpRequestMessage, HttpResponseMessage> respond)
            => new((req, _) => respond(req));

        public static Responder Sequence(params Func<HttpResponseMessage>[] responses)
            => new((_, call) => responses[Math.Min(call, responses.Length - 1)]());

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
        {
            var index = Calls;
            Calls++;
            return Task.FromResult(_respond(request, index));
        }
    }
}
