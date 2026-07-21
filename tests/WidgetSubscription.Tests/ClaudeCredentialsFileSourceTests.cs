using WidgetSubscription.Providers.ClaudeCode;
using Xunit;

namespace WidgetSubscription.Tests;

/// <summary>
/// The MVP credential source over a real temp file. Verifies it reads the token and expiry,
/// yields <c>null</c> (not an exception) for every absence/corruption, and never mutates the file.
/// </summary>
public sealed class ClaudeCredentialsFileSourceTests : IDisposable
{
    private readonly string _path = Path.Combine(
        Path.GetTempPath(), $"creds-{Guid.NewGuid():N}.json");

    [Fact]
    public async Task Reads_access_token_and_expiry()
    {
        // 2026-07-21T00:00:00Z in epoch ms.
        var expiresMs = new DateTimeOffset(2026, 7, 21, 0, 0, 0, TimeSpan.Zero).ToUnixTimeMilliseconds();
        await File.WriteAllTextAsync(_path, $$"""
        { "claudeAiOauth": { "accessToken": "sk-ant-oat01-abc", "expiresAt": {{expiresMs}} } }
        """);
        var source = new ClaudeCredentialsFileSource(_path);

        var token = await source.GetAsync(CancellationToken.None);

        Assert.NotNull(token);
        Assert.Equal("sk-ant-oat01-abc", token!.Value);
        Assert.Equal(DateTimeOffset.FromUnixTimeMilliseconds(expiresMs), token.ExpiresAt);
    }

    [Fact]
    public async Task Missing_file_yields_null()
    {
        var source = new ClaudeCredentialsFileSource(_path);
        Assert.Null(await source.GetAsync(CancellationToken.None));
    }

    [Fact]
    public async Task Missing_token_field_yields_null()
    {
        await File.WriteAllTextAsync(_path, """{ "claudeAiOauth": { "refreshToken": "x" } }""");
        var source = new ClaudeCredentialsFileSource(_path);
        Assert.Null(await source.GetAsync(CancellationToken.None));
    }

    [Fact]
    public async Task Corrupt_json_yields_null()
    {
        await File.WriteAllTextAsync(_path, "{ not json");
        var source = new ClaudeCredentialsFileSource(_path);
        Assert.Null(await source.GetAsync(CancellationToken.None));
    }

    [Fact]
    public async Task Reading_does_not_modify_the_file()
    {
        const string original = """{ "claudeAiOauth": { "accessToken": "tok" } }""";
        await File.WriteAllTextAsync(_path, original);
        var before = File.GetLastWriteTimeUtc(_path);
        var source = new ClaudeCredentialsFileSource(_path);

        await source.GetAsync(CancellationToken.None);

        Assert.Equal(original, await File.ReadAllTextAsync(_path));
        Assert.Equal(before, File.GetLastWriteTimeUtc(_path));
    }

    public void Dispose()
    {
        if (File.Exists(_path))
            File.Delete(_path);
    }
}
