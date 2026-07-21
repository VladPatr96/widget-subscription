using System.Text.Json;

namespace WidgetSubscription.Providers.ClaudeCode;

/// <summary>
/// The MVP <see cref="ICredentialSource"/>: reads the Claude Code OAuth access token from
/// <c>~/.claude/.credentials.json</c>, strictly read-only. It never writes or refreshes the
/// file — Claude Code owns the token lifecycle. Any absence or corruption yields <c>null</c>
/// (which the adapter turns into <see cref="Core.FetchErrorKind.NoCredentials"/>), never an
/// exception.
/// </summary>
public sealed class ClaudeCredentialsFileSource : ICredentialSource
{
    private readonly string _path;

    public ClaudeCredentialsFileSource(string? path = null)
        => _path = path ?? DefaultPath();

    private static string DefaultPath()
    {
        var home = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        return Path.Combine(home, ".claude", ".credentials.json");
    }

    public async Task<AccessToken?> GetAsync(CancellationToken ct)
    {
        if (!File.Exists(_path))
            return null;

        string json;
        try
        {
            json = await File.ReadAllTextAsync(_path, ct).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return null;
        }

        try
        {
            using var doc = JsonDocument.Parse(json);
            if (!doc.RootElement.TryGetProperty("claudeAiOauth", out var oauth)
                || oauth.ValueKind != JsonValueKind.Object
                || !oauth.TryGetProperty("accessToken", out var tokenEl)
                || tokenEl.ValueKind != JsonValueKind.String)
                return null;

            var value = tokenEl.GetString();
            if (string.IsNullOrEmpty(value))
                return null;

            DateTimeOffset? expiresAt = null;
            if (oauth.TryGetProperty("expiresAt", out var expiry)
                && expiry.ValueKind == JsonValueKind.Number
                && expiry.TryGetInt64(out var epochMs))
                expiresAt = DateTimeOffset.FromUnixTimeMilliseconds(epochMs);

            return new AccessToken(value, expiresAt);
        }
        catch (JsonException)
        {
            return null;
        }
    }
}
