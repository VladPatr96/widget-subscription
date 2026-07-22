using System.Net;
using System.Net.Sockets;
using WidgetSubscription.Providers.ClaudeCode;
using Xunit;

namespace WidgetSubscription.Tests;

/// <summary>
/// The real loopback receiver over a live in-process <see cref="System.Net.HttpListener"/> — no
/// browser. Binds an ephemeral port, drives the redirect with an <see cref="HttpClient"/>, and
/// verifies the callback parameters are captured (#18 §1a).
/// </summary>
public sealed class HttpLoopbackListenerTests
{
    [Fact]
    public async Task Captures_code_and_state_from_the_redirect()
    {
        var factory = new HttpLoopbackListenerFactory(FreePort());
        using var listener = factory.TryStart();
        Assert.NotNull(listener);

        var waiting = listener!.WaitForCallbackAsync(CancellationToken.None);
        using var http = new HttpClient();
        using var response = await http.GetAsync($"{listener.RedirectUri}?code=the-code&state=the-state");

        var callback = await waiting;
        Assert.True(response.IsSuccessStatusCode);
        Assert.Equal("the-code", callback.Code);
        Assert.Equal("the-state", callback.State);
        Assert.Null(callback.Error);
    }

    [Fact]
    public async Task Surfaces_the_error_parameter()
    {
        var factory = new HttpLoopbackListenerFactory(FreePort());
        using var listener = factory.TryStart();
        Assert.NotNull(listener);

        var waiting = listener!.WaitForCallbackAsync(CancellationToken.None);
        using var http = new HttpClient();
        using var response = await http.GetAsync($"{listener.RedirectUri}?error=access_denied");

        var callback = await waiting;
        Assert.Equal("access_denied", callback.Error);
        Assert.Null(callback.Code);
    }

    [Fact]
    public async Task Cancellation_stops_waiting()
    {
        var factory = new HttpLoopbackListenerFactory(FreePort());
        using var listener = factory.TryStart();
        Assert.NotNull(listener);

        using var cts = new CancellationTokenSource();
        var waiting = listener!.WaitForCallbackAsync(cts.Token);
        cts.Cancel();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(async () => await waiting);
    }

    // A free loopback port for the test — production uses Claude Code's fixed 54545.
    private static int FreePort()
    {
        using var socket = new Socket(AddressFamily.InterNetwork, SocketType.Stream, ProtocolType.Tcp);
        socket.Bind(new IPEndPoint(IPAddress.Loopback, 0));
        return ((IPEndPoint)socket.LocalEndPoint!).Port;
    }
}
