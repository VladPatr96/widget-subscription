using System.Diagnostics;
using WidgetSubscription.Providers.ClaudeCode;

namespace WidgetSubscription.App;

/// <summary>
/// Console harness for diagnosing the interactive own-login flow (run with <c>--login</c>). Runs the
/// real <see cref="WidgetLogin"/> against the live endpoints but logs every step — the authorize URL
/// it opens and the full token-endpoint request/response — and reads the paste code from stdin.
/// Not part of the shipped UX; a troubleshooting tool.
/// </summary>
public static class LoginDiagnostic
{
    public static async Task<int> RunAsync()
    {
        Console.WriteLine("== Login diagnostic (real endpoints) ==");
        using var http = new HttpClient(new LoggingHandler());
        var store = new WidgetTokenFileStore(Path.Combine(Path.GetTempPath(), "widget-logindiag.json"));
        var login = new WidgetLogin(
            http, store, new LoggingBrowser(), new HttpLoopbackListenerFactory(), new ConsoleCodeEntry());

        var result = await login.LoginAsync(CancellationToken.None);
        Console.WriteLine($"RESULT: {result}");
        return result is LoginResult.Success ? 0 : 1;
    }

    private sealed class LoggingBrowser : IBrowserLauncher
    {
        public void Open(string url)
        {
            Console.WriteLine($"[authorize] {url}");
            Process.Start(new ProcessStartInfo(url) { UseShellExecute = true });
        }
    }

    private sealed class ConsoleCodeEntry : ICodeEntry
    {
        public Task<string?> PromptAsync(CancellationToken ct)
        {
            Console.Write("Вставьте код из браузера (Enter — отмена): ");
            var line = Console.ReadLine();
            return Task.FromResult(string.IsNullOrWhiteSpace(line) ? null : line.Trim());
        }
    }

    private sealed class LoggingHandler : DelegatingHandler
    {
        public LoggingHandler() : base(new SocketsHttpHandler()) { }

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
        {
            Console.WriteLine($"[http] {request.Method} {request.RequestUri}");
            if (request.Content is not null)
                Console.WriteLine($"[http] request body: {await request.Content.ReadAsStringAsync(ct)}");

            var response = await base.SendAsync(request, ct).ConfigureAwait(false);
            var body = response.Content is null ? "" : await response.Content.ReadAsStringAsync(ct);
            Console.WriteLine($"[http] -> {(int)response.StatusCode} {response.StatusCode}");
            Console.WriteLine($"[http] response body: {body}");

            // Re-provide the consumed body so WidgetLogin can read it again.
            var replayed = new HttpResponseMessage(response.StatusCode)
            {
                Content = new StringContent(body),
            };
            return replayed;
        }
    }
}
