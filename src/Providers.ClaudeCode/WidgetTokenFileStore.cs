using System.Text.Json;
using System.Text.Json.Serialization;

namespace WidgetSubscription.Providers.ClaudeCode;

/// <summary>
/// File-backed <see cref="IWidgetTokenStore"/> over
/// <c>%LOCALAPPDATA%\WidgetSubscription\credentials.json</c> (Local, not Roaming — #16/#19).
/// The token set lives under a <c>widgetOauth</c> key so the file is unambiguously
/// <em>not</em> Claude Code's; timestamps are stored as epoch milliseconds, matching the
/// shape Claude Code uses. Stored plaintext — parity with Claude Code, protected by the
/// user folder ACL (#19 §2). Writes are atomic (temp file + replace) so a torn write can
/// never lose a rotated refresh token (#19 §4). Any absence or corruption yields
/// <c>null</c> from <see cref="Load"/>, never an exception.
/// </summary>
public sealed class WidgetTokenFileStore : IWidgetTokenStore
{
    private static readonly JsonSerializerOptions SerializerOptions =
        new() { WriteIndented = true };

    private readonly string _path;

    public WidgetTokenFileStore(string? path = null)
        => _path = path ?? DefaultPath();

    private static string DefaultPath()
    {
        var local = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
        return Path.Combine(local, "WidgetSubscription", "credentials.json");
    }

    public WidgetTokens? Load()
    {
        if (!File.Exists(_path))
            return null;

        string json;
        try
        {
            json = File.ReadAllText(_path);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return null;
        }

        try
        {
            using var doc = JsonDocument.Parse(json);
            if (doc.RootElement.ValueKind != JsonValueKind.Object
                || !doc.RootElement.TryGetProperty("widgetOauth", out var oauth)
                || oauth.ValueKind != JsonValueKind.Object)
                return null;

            var access = ReadNonEmptyString(oauth, "accessToken");
            var refresh = ReadNonEmptyString(oauth, "refreshToken");
            if (access is null || refresh is null)
                return null;

            return new WidgetTokens(
                access,
                refresh,
                ReadEpoch(oauth, "expiresAt"),
                ReadEpoch(oauth, "refreshTokenExpiresAt"),
                ReadNonEmptyString(oauth, "scope") ?? string.Empty);
        }
        catch (JsonException)
        {
            return null;
        }
    }

    public void Save(WidgetTokens tokens)
    {
        var directory = Path.GetDirectoryName(_path);
        if (!string.IsNullOrEmpty(directory))
            Directory.CreateDirectory(directory);

        var envelope = new Envelope(new Dto(
            tokens.AccessToken,
            tokens.RefreshToken,
            tokens.ExpiresAt.ToUnixTimeMilliseconds(),
            tokens.RefreshTokenExpiresAt.ToUnixTimeMilliseconds(),
            tokens.Scope));
        var json = JsonSerializer.Serialize(envelope, SerializerOptions);

        var temp = _path + ".tmp-" + Guid.NewGuid().ToString("N");
        try
        {
            File.WriteAllText(temp, json);
            File.Move(temp, _path, overwrite: true);
        }
        catch
        {
            TryDelete(temp);
            throw;
        }
    }

    public void Clear()
    {
        try
        {
            if (File.Exists(_path))
                File.Delete(_path);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // Best-effort: a locked/removed file leaves nothing usable to load anyway.
        }
    }

    private static string? ReadNonEmptyString(JsonElement obj, string name)
        => obj.TryGetProperty(name, out var el)
            && el.ValueKind == JsonValueKind.String
            && el.GetString() is { Length: > 0 } value
            ? value
            : null;

    private static DateTimeOffset ReadEpoch(JsonElement obj, string name)
        => obj.TryGetProperty(name, out var el)
            && el.ValueKind == JsonValueKind.Number
            && el.TryGetInt64(out var ms)
            ? DateTimeOffset.FromUnixTimeMilliseconds(ms)
            : DateTimeOffset.FromUnixTimeMilliseconds(0);

    private static void TryDelete(string path)
    {
        try
        {
            if (File.Exists(path))
                File.Delete(path);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // Nothing further to do for a leftover temp file.
        }
    }

    private sealed record Envelope(
        [property: JsonPropertyName("widgetOauth")] Dto WidgetOauth);

    private sealed record Dto(
        [property: JsonPropertyName("accessToken")] string AccessToken,
        [property: JsonPropertyName("refreshToken")] string RefreshToken,
        [property: JsonPropertyName("expiresAt")] long ExpiresAt,
        [property: JsonPropertyName("refreshTokenExpiresAt")] long RefreshTokenExpiresAt,
        [property: JsonPropertyName("scope")] string Scope);
}
