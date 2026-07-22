using System.Security.Cryptography;
using System.Text;

namespace WidgetSubscription.Providers.ClaudeCode;

/// <summary>
/// A PKCE (RFC 7636, S256) pair for one login attempt (#14 §2). The <see cref="State"/> equals the
/// <see cref="Verifier"/> — the observed practice of this flow — so the redirect's <c>state</c> can
/// be checked against it. Each attempt mints a fresh pair; a retry never reuses one.
/// </summary>
internal sealed record Pkce(string Verifier, string Challenge, string State)
{
    public static Pkce Create()
    {
        var verifier = Base64Url(RandomNumberGenerator.GetBytes(32));
        var challenge = Base64Url(SHA256.HashData(Encoding.ASCII.GetBytes(verifier)));
        return new Pkce(verifier, challenge, verifier);
    }

    private static string Base64Url(byte[] data)
        => Convert.ToBase64String(data).TrimEnd('=').Replace('+', '-').Replace('/', '_');
}
