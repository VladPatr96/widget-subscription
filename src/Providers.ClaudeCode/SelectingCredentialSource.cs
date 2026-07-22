namespace WidgetSubscription.Providers.ClaudeCode;

/// <summary>
/// Composite <see cref="ICredentialSource"/> that resolves the active source per call from the
/// persisted <see cref="CredentialMode"/> (#17 §1, §4): <see cref="CredentialMode.Own"/> forces
/// own-login, <see cref="CredentialMode.Borrow"/> forces Claude Code, and <see cref="CredentialMode.Auto"/>
/// prefers borrow when it yields a token (Claude Code present — live or expired, which degrades via
/// the 401 path) and falls back to own-login only when borrow yields nothing (Claude Code absent).
/// The adapter sees a single <see cref="ICredentialSource"/>; the two-mode split stays provider-local.
/// </summary>
/// <remarks>
/// Also forwards <see cref="ICredentialInvalidation.Invalidate"/> to whichever source was last
/// resolved, so the adapter's 401 path can force the own-login source to refresh (spec §4.3). Without
/// this forward the invalidation would be a silent no-op once the composite sits in front.
/// </remarks>
public sealed class SelectingCredentialSource : ICredentialSource, ICredentialInvalidation
{
    private readonly ICredentialSource _borrow;
    private readonly ICredentialSource _own;
    private readonly ICredentialModeStore _mode;
    private ICredentialSource? _active;

    public SelectingCredentialSource(ICredentialSource borrow, ICredentialSource own, ICredentialModeStore mode)
    {
        _borrow = borrow;
        _own = own;
        _mode = mode;
    }

    public async Task<AccessToken?> GetAsync(CancellationToken ct)
    {
        switch (_mode.Get())
        {
            case CredentialMode.Own:
                _active = _own;
                return await _own.GetAsync(ct).ConfigureAwait(false);

            case CredentialMode.Borrow:
                _active = _borrow;
                return await _borrow.GetAsync(ct).ConfigureAwait(false);

            default: // Auto: borrow if Claude Code yields a token, else own-login.
                var borrowed = await _borrow.GetAsync(ct).ConfigureAwait(false);
                if (borrowed is not null)
                {
                    _active = _borrow;
                    return borrowed;
                }
                _active = _own;
                return await _own.GetAsync(ct).ConfigureAwait(false);
        }
    }

    public void Invalidate() => (_active as ICredentialInvalidation)?.Invalidate();
}
