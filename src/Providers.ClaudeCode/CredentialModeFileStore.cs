namespace WidgetSubscription.Providers.ClaudeCode;

/// <summary>
/// File-backed <see cref="ICredentialModeStore"/> over <c>%LOCALAPPDATA%\WidgetSubscription\mode.txt</c>
/// (Local, beside the own token file — #16/#19). Stores the mode name as plaintext; any absence or
/// unparseable content reads back as <see cref="CredentialMode.Auto"/>, never an exception. Writes
/// are atomic (temp file + replace).
/// </summary>
public sealed class CredentialModeFileStore : ICredentialModeStore
{
    private readonly string _path;

    public CredentialModeFileStore(string? path = null)
        => _path = path ?? DefaultPath();

    private static string DefaultPath()
    {
        var local = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
        return Path.Combine(local, "WidgetSubscription", "mode.txt");
    }

    public CredentialMode Get()
    {
        try
        {
            if (!File.Exists(_path))
                return CredentialMode.Auto;
            var text = File.ReadAllText(_path).Trim();
            return Enum.TryParse<CredentialMode>(text, ignoreCase: true, out var mode)
                ? mode
                : CredentialMode.Auto;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return CredentialMode.Auto;
        }
    }

    public void Set(CredentialMode mode)
    {
        var directory = Path.GetDirectoryName(_path);
        if (!string.IsNullOrEmpty(directory))
            Directory.CreateDirectory(directory);

        var temp = _path + ".tmp-" + Guid.NewGuid().ToString("N");
        try
        {
            File.WriteAllText(temp, mode.ToString());
            File.Move(temp, _path, overwrite: true);
        }
        catch
        {
            TryDelete(temp);
            throw;
        }
    }

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
}
