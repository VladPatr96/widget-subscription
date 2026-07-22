using System.Net;
using System.Net.Sockets;
using System.Text;

namespace WidgetSubscription.Providers.ClaudeCode;

/// <summary>
/// Binds an ephemeral loopback port and serves the single OAuth redirect (RFC 8252, #18 §1a).
/// Returns <c>null</c> from <see cref="TryStart"/> when no port can be bound so the caller falls
/// back to hosted-paste (#18 §3.4).
/// </summary>
public sealed class HttpLoopbackListenerFactory : ILoopbackListenerFactory
{
    public ILoopbackListener? TryStart()
    {
        try
        {
            var port = FreeLoopbackPort();
            var listener = new HttpListener();
            listener.Prefixes.Add($"http://localhost:{port}/");
            listener.Start();
            return new HttpLoopbackListener(listener, port);
        }
        catch (Exception ex) when (ex is HttpListenerException or SocketException)
        {
            return null;
        }
    }

    private static int FreeLoopbackPort()
    {
        using var socket = new Socket(AddressFamily.InterNetwork, SocketType.Stream, ProtocolType.Tcp);
        socket.Bind(new IPEndPoint(IPAddress.Loopback, 0));
        return ((IPEndPoint)socket.LocalEndPoint!).Port;
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
