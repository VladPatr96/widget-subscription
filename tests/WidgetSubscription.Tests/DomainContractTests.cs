using WidgetSubscription.Core;
using WidgetSubscription.Providers.ClaudeCode;
using Xunit;

namespace WidgetSubscription.Tests;

/// <summary>
/// Foundation-level checks over the domain contract this ticket delivers (#9): the
/// record shapes and the closed <see cref="FetchResult"/> hierarchy. Behavior (fetching,
/// caching, presentation) lands in later tickets and is tested there.
/// </summary>
public class DomainContractTests
{
    [Fact]
    public void Limit_carries_headroom_not_percent()
    {
        var resetsAt = new DateTimeOffset(2026, 7, 20, 12, 0, 0, TimeSpan.Zero);
        var limit = new Limit(LimitKind.WeeklyScoped, "Fable 5", Headroom: 42.5, resetsAt, IsActive: true);

        Assert.Equal(LimitKind.WeeklyScoped, limit.Kind);
        Assert.Equal("Fable 5", limit.DisplayName);
        Assert.Equal(42.5, limit.Headroom);
        Assert.Equal(resetsAt, limit.ResetsAt);
        Assert.True(limit.IsActive);
    }

    [Fact]
    public void FetchResult_is_a_closed_success_or_failure_envelope()
    {
        var snapshot = new UsageSnapshot(new[]
        {
            new Limit(LimitKind.Session, "5-hour", 90, DateTimeOffset.UnixEpoch, true),
        }, DateTimeOffset.UnixEpoch);

        FetchResult success = new FetchResult.Success(snapshot);
        FetchResult failure = new FetchResult.Failure(
            new FetchError(FetchErrorKind.NoCredentials, "no credentials"));

        Assert.Equal("no credentials", Describe(failure));
        Assert.Equal("1 limit(s)", Describe(success));

        static string Describe(FetchResult result) => result switch
        {
            FetchResult.Success s => $"{s.Snapshot.Limits.Count} limit(s)",
            FetchResult.Failure f => f.Error.Message,
            _ => throw new InvalidOperationException("FetchResult must be Success or Failure"),
        };
    }

    [Fact]
    public void ProviderInfo_identity_is_available_without_a_fetch()
    {
        var info = new ProviderInfo("claude-code", "Claude Code", "#D97757");

        Assert.Equal("claude-code", info.Id);
        Assert.Equal("Claude Code", info.DisplayName);
        Assert.Equal("#D97757", info.BrandColor);
    }

    [Fact]
    public async Task ICredentialSource_can_yield_a_token_or_null()
    {
        ICredentialSource present = new StubCredentialSource(
            new AccessToken("token-value", ExpiresAt: null));
        ICredentialSource absent = new StubCredentialSource(null);

        Assert.Equal("token-value", (await present.GetAsync(CancellationToken.None))!.Value);
        Assert.Null(await absent.GetAsync(CancellationToken.None));
    }

    private sealed class StubCredentialSource(AccessToken? token) : ICredentialSource
    {
        public Task<AccessToken?> GetAsync(CancellationToken ct) => Task.FromResult(token);
    }
}
