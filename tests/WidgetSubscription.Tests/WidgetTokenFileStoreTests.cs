using System.Text.Json;
using WidgetSubscription.Providers.ClaudeCode;
using Xunit;

namespace WidgetSubscription.Tests;

/// <summary>
/// The own-login token store over a real temp file. Verifies round-trip fidelity (incl.
/// exact epoch-ms timestamps), that every absence/corruption yields <c>null</c> rather than
/// an exception, that <see cref="WidgetTokenFileStore.Save"/> is atomic-replace and creates
/// its directory, that the file is plaintext under a <c>widgetOauth</c> key, and that
/// <see cref="WidgetTokenFileStore.Clear"/> removes it.
/// </summary>
public sealed class WidgetTokenFileStoreTests : IDisposable
{
    private readonly string _dir = Path.Combine(
        Path.GetTempPath(), $"widget-store-{Guid.NewGuid():N}");

    private string Path_ => Path.Combine(_dir, "WidgetSubscription", "credentials.json");

    private static WidgetTokens Sample() => new(
        AccessToken: "sk-ant-oat01-abc",
        RefreshToken: "sk-ant-ort01-xyz",
        // Whole-millisecond instants so the epoch-ms round-trip is exact.
        ExpiresAt: DateTimeOffset.FromUnixTimeMilliseconds(1_800_000_000_000),
        RefreshTokenExpiresAt: DateTimeOffset.FromUnixTimeMilliseconds(1_900_000_000_000),
        Scope: "user:inference user:profile");

    [Fact]
    public void Save_then_load_round_trips_all_fields()
    {
        var store = new WidgetTokenFileStore(Path_);
        var tokens = Sample();

        store.Save(tokens);
        var loaded = store.Load();

        Assert.Equal(tokens, loaded);
    }

    [Fact]
    public void Save_creates_missing_directory()
    {
        Assert.False(Directory.Exists(_dir));
        var store = new WidgetTokenFileStore(Path_);

        store.Save(Sample());

        Assert.True(File.Exists(Path_));
    }

    [Fact]
    public void Stored_file_is_plaintext_under_widgetOauth_key()
    {
        var store = new WidgetTokenFileStore(Path_);
        store.Save(Sample());

        var json = File.ReadAllText(Path_);
        Assert.Contains("sk-ant-oat01-abc", json);          // plaintext, not encrypted
        using var doc = JsonDocument.Parse(json);
        Assert.True(doc.RootElement.TryGetProperty("widgetOauth", out var oauth));
        Assert.Equal("sk-ant-ort01-xyz", oauth.GetProperty("refreshToken").GetString());
        Assert.Equal(1_800_000_000_000, oauth.GetProperty("expiresAt").GetInt64());
    }

    [Fact]
    public void Save_replaces_an_existing_file()
    {
        var store = new WidgetTokenFileStore(Path_);
        store.Save(Sample());
        var second = Sample() with { AccessToken = "sk-ant-oat01-second", RefreshToken = "sk-ant-ort01-second" };

        store.Save(second);

        Assert.Equal(second, store.Load());
    }

    [Fact]
    public void Missing_file_loads_null()
    {
        var store = new WidgetTokenFileStore(Path_);
        Assert.Null(store.Load());
    }

    [Fact]
    public void Corrupt_json_loads_null()
    {
        Directory.CreateDirectory(System.IO.Path.GetDirectoryName(Path_)!);
        File.WriteAllText(Path_, "{ not json");
        var store = new WidgetTokenFileStore(Path_);

        Assert.Null(store.Load());
    }

    [Fact]
    public void Missing_refresh_token_loads_null()
    {
        Directory.CreateDirectory(System.IO.Path.GetDirectoryName(Path_)!);
        File.WriteAllText(Path_, """{ "widgetOauth": { "accessToken": "a", "scope": "s" } }""");
        var store = new WidgetTokenFileStore(Path_);

        Assert.Null(store.Load());
    }

    [Fact]
    public void Wrong_envelope_key_loads_null()
    {
        // A Claude Code credentials file must never be mistaken for the widget's own store.
        Directory.CreateDirectory(System.IO.Path.GetDirectoryName(Path_)!);
        File.WriteAllText(Path_, """{ "claudeAiOauth": { "accessToken": "a", "refreshToken": "r" } }""");
        var store = new WidgetTokenFileStore(Path_);

        Assert.Null(store.Load());
    }

    [Fact]
    public void Non_object_json_root_loads_null()
    {
        // Valid JSON whose root is an array/scalar must not throw from TryGetProperty.
        Directory.CreateDirectory(System.IO.Path.GetDirectoryName(Path_)!);
        File.WriteAllText(Path_, "[]");
        var store = new WidgetTokenFileStore(Path_);

        Assert.Null(store.Load());
    }

    [Fact]
    public void Clear_removes_the_file()
    {
        var store = new WidgetTokenFileStore(Path_);
        store.Save(Sample());
        Assert.True(File.Exists(Path_));

        store.Clear();

        Assert.False(File.Exists(Path_));
        Assert.Null(store.Load());
    }

    [Fact]
    public void Clear_on_absent_file_is_a_no_op()
    {
        var store = new WidgetTokenFileStore(Path_);
        store.Clear(); // must not throw
        Assert.Null(store.Load());
    }

    public void Dispose()
    {
        if (Directory.Exists(_dir))
            Directory.Delete(_dir, recursive: true);
    }
}
