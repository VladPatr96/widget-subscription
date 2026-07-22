namespace WidgetSubscription.Providers.ClaudeCode;

/// <summary>
/// Which credential source the widget uses (#17 §1). <see cref="Auto"/> is the default: borrow when
/// Claude Code is present, otherwise own-login. <see cref="Borrow"/>/<see cref="Own"/> are the
/// explicit, sticky choices set by the tray "source of login" toggle.
/// </summary>
public enum CredentialMode
{
    Auto,
    Borrow,
    Own,
}

/// <summary>
/// Persists the source-of-login preference behind the tray toggle. Returns <see cref="CredentialMode.Auto"/>
/// when unset or unreadable. Lives beside the adapter (the mode flag storage detail deferred here from #17/#19).
/// </summary>
public interface ICredentialModeStore
{
    CredentialMode Get();
    void Set(CredentialMode mode);
}
