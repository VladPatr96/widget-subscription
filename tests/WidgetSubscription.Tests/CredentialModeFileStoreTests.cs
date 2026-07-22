using WidgetSubscription.Providers.ClaudeCode;
using Xunit;

namespace WidgetSubscription.Tests;

/// <summary>
/// The file-backed mode store (#17 §1) over a real temp file: defaults to Auto when absent or
/// unparseable, round-trips explicit choices, and creates its directory on first write.
/// </summary>
public sealed class CredentialModeFileStoreTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), $"mode-{Guid.NewGuid():N}");

    private string Path_ => Path.Combine(_dir, "WidgetSubscription", "mode.txt");

    [Fact]
    public void Absent_file_reads_as_auto()
        => Assert.Equal(CredentialMode.Auto, new CredentialModeFileStore(Path_).Get());

    [Theory]
    [InlineData(CredentialMode.Auto)]
    [InlineData(CredentialMode.Borrow)]
    [InlineData(CredentialMode.Own)]
    public void Set_then_get_round_trips(CredentialMode mode)
    {
        var store = new CredentialModeFileStore(Path_);

        store.Set(mode);

        Assert.Equal(mode, new CredentialModeFileStore(Path_).Get()); // survives a fresh instance
    }

    [Fact]
    public void Set_creates_the_directory()
    {
        Assert.False(Directory.Exists(_dir));
        new CredentialModeFileStore(Path_).Set(CredentialMode.Own);
        Assert.True(File.Exists(Path_));
    }

    [Fact]
    public void Unparseable_content_reads_as_auto()
    {
        Directory.CreateDirectory(System.IO.Path.GetDirectoryName(Path_)!);
        File.WriteAllText(Path_, "gibberish");
        Assert.Equal(CredentialMode.Auto, new CredentialModeFileStore(Path_).Get());
    }

    public void Dispose()
    {
        if (Directory.Exists(_dir))
            Directory.Delete(_dir, recursive: true);
    }
}
