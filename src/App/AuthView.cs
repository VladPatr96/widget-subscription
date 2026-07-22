namespace WidgetSubscription.App;

/// <summary>
/// App-level auth affordances layered over the Core <see cref="WidgetSubscription.Core.PanelView"/>
/// (Core stays auth-agnostic, #17 §4). Computed by <see cref="TrayController"/> from the current
/// credential mode and own-token presence.
/// </summary>
/// <param name="LoginRequired">Show the empty "sign in with Anthropic" state instead of the bars.</param>
/// <param name="CanSignOut">An own-login grant exists, so offer "sign out".</param>
/// <param name="SourceIsOwn">The active source is own-login (labels the source toggle).</param>
/// <param name="Notice">Optional footer override (e.g. a transient error or a "Claude Code not found" hint).</param>
public sealed record AuthView(bool LoginRequired, bool CanSignOut, bool SourceIsOwn, string? Notice);
