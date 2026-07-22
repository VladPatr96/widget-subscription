using System.Net;
using System.Text;

namespace WidgetSubscription.Providers.ClaudeCode;

/// <summary>
/// Binds Claude Code's registered loopback callback port for the OAuth redirect (#18 §1a).
/// claude.ai validates <c>redirect_uri</c> exactly and does <em>not</em> ignore the port (contrary to
/// RFC 8252), so this must be the fixed port the public client <c>9d1c250a</c> registered
/// (<see cref="DefaultPort"/>), not an arbitrary ephemeral one — otherwise authorize is rejected.
/// Returns <c>null</c> from <see cref="TryStart"/> when the port cannot be bound (already in use),
/// so the caller falls back to hosted-paste (#18 §3.4). The port is injectable for tests.
/// </summary>
public sealed class HttpLoopbackListenerFactory : ILoopbackListenerFactory
{
    public const int DefaultPort = 54545;

    private readonly int _port;

    public HttpLoopbackListenerFactory(int port = DefaultPort) => _port = port;

    public ILoopbackListener? TryStart()
    {
        try
        {
            var listener = new HttpListener();
            listener.Prefixes.Add($"http://localhost:{_port}/");
            listener.Start();
            return new HttpLoopbackListener(listener, _port);
        }
        catch (HttpListenerException)
        {
            return null;
        }
    }
}

/// <summary>A bound <see cref="HttpListener"/> that resolves once the browser hits its callback.</summary>
public sealed class HttpLoopbackListener : ILoopbackListener
{
    private readonly HttpListener _listener;

    internal HttpLoopbackListener(HttpListener listener, int port)
    {
        _listener = listener;
        RedirectUri = $"http://localhost:{port}/callback";
    }

    public string RedirectUri { get; }

    public async Task<LoopbackCallback> WaitForCallbackAsync(CancellationToken ct)
    {
        using var registration = ct.Register(() =>
        {
            try { _listener.Stop(); } catch (ObjectDisposedException) { /* already gone */ }
        });

        HttpListenerContext context;
        try
        {
            context = await _listener.GetContextAsync().ConfigureAwait(false);
        }
        catch (Exception) when (ct.IsCancellationRequested)
        {
            throw new OperationCanceledException(ct);
        }

        var query = context.Request.QueryString;
        var error = query["error"];
        await RespondAsync(context.Response, error is null).ConfigureAwait(false);
        return new LoopbackCallback(query["code"], query["state"], error);
    }

    public void Dispose()
    {
        try { _listener.Close(); } catch (ObjectDisposedException) { /* already closed */ }
    }

    private static async Task RespondAsync(HttpListenerResponse response, bool ok)
    {
        var html = ok
            ? "<html><body>Вход выполнен — можно вернуться в виджет.</body></html>"
            : "<html><body>Не удалось войти. Вернитесь в виджет и повторите.</body></html>";
        var bytes = Encoding.UTF8.GetBytes(html);
        response.ContentType = "text/html; charset=utf-8";
        response.ContentLength64 = bytes.Length;
        try
        {
            await response.OutputStream.WriteAsync(bytes).ConfigureAwait(false);
        }
        finally
        {
            response.Close();
        }
    }
}
