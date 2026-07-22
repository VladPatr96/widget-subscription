using System.Net;
using System.Net.Http.Headers;
using System.Text.Json;
using WidgetSubscription.Core;

namespace WidgetSubscription.Providers.ClaudeCode;

/// <summary>
/// The Claude Code implementation of <see cref="IUsageProvider"/>. Calls the OAuth usage
/// endpoint, reuses the token from an injected <see cref="ICredentialSource"/> (read-only),
/// and maps the response onto the three domain <see cref="Limit"/>s. Routine failures are
/// returned as <see cref="FetchResult.Failure"/>, never thrown; only a caller-driven
/// cancellation propagates. The 401 reread+retry is encapsulated here — <see
/// cref="FetchErrorKind.Unauthorized"/> escapes only when the retry also fails.
/// </summary>
public sealed class ClaudeCodeAdapter : IUsageProvider
{
    private const string UsageUrl = "https://api.anthropic.com/api/oauth/usage";

    private static readonly ProviderInfo ClaudeCode =
        new("claude-code", "Claude Code", "#D97757");

    private readonly HttpClient _http;
    private readonly ICredentialSource _credentials;
    private readonly TimeProvider _time;

    public ClaudeCodeAdapter(HttpClient http, ICredentialSource credentials, TimeProvider? time = null)
    {
        _http = http;
        _credentials = credentials;
        _time = time ?? TimeProvider.System;
    }

    public ProviderInfo Info => ClaudeCode;

    public async Task<FetchResult> FetchAsync(CancellationToken ct)
    {
        var token = await _credentials.GetAsync(ct).ConfigureAwait(false);
        if (token is null)
            return Fail(FetchErrorKind.NoCredentials, "Учётные данные Claude Code не найдены.");

        HttpResponseMessage response;
        try
        {
            response = await SendAsync(token, ct).ConfigureAwait(false);

            if (response.StatusCode == HttpStatusCode.Unauthorized)
            {
                // The token may have gone stale; Claude Code could have refreshed it, or the
                // own-login source can refresh its own grant. Hint the source that this token was
                // rejected, then re-read once and retry before degrading.
                response.Dispose();
                (_credentials as ICredentialInvalidation)?.Invalidate();
                token = await _credentials.GetAsync(ct).ConfigureAwait(false);
                if (token is null)
                    return Fail(FetchErrorKind.Unauthorized,
                        "Токен недействителен, а учётные данные больше недоступны.");
                response = await SendAsync(token, ct).ConfigureAwait(false);
            }
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            throw;
        }
        catch (OperationCanceledException)
        {
            return Fail(FetchErrorKind.Timeout, "Превышено время ожидания ответа Claude Code.");
        }
        catch (HttpRequestException ex)
        {
            return Fail(FetchErrorKind.SourceUnavailable, $"Источник Claude Code недоступен: {ex.Message}");
        }

        using (response)
        {
            if (response.StatusCode == HttpStatusCode.Unauthorized)
                return Fail(FetchErrorKind.Unauthorized, "Токен Claude Code недействителен.");

            if (!response.IsSuccessStatusCode)
                return Fail(FetchErrorKind.SourceUnavailable,
                    $"Claude Code вернул {(int)response.StatusCode}.",
                    RetryAfterOf(response));

            string body;
            try
            {
                body = await response.Content.ReadAsStringAsync(ct).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested)
            {
                throw;
            }
            catch (OperationCanceledException)
            {
                return Fail(FetchErrorKind.Timeout, "Превышено время ожидания тела ответа.");
            }
            catch (HttpRequestException ex)
            {
                return Fail(FetchErrorKind.SourceUnavailable, $"Ошибка чтения ответа: {ex.Message}");
            }

            return Parse(body);
        }
    }

    private Task<HttpResponseMessage> SendAsync(AccessToken token, CancellationToken ct)
    {
        var request = new HttpRequestMessage(HttpMethod.Get, UsageUrl);
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token.Value);
        request.Headers.TryAddWithoutValidation("anthropic-beta", "oauth-2025-04-20");
        request.Headers.TryAddWithoutValidation("anthropic-version", "2023-06-01");
        return _http.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, ct);
    }

    private TimeSpan? RetryAfterOf(HttpResponseMessage response)
    {
        var retryAfter = response.Headers.RetryAfter;
        if (retryAfter is null)
            return null;
        if (retryAfter.Delta is TimeSpan delta)
            return delta;
        if (retryAfter.Date is DateTimeOffset date)
        {
            var remaining = date - _time.GetUtcNow();
            return remaining > TimeSpan.Zero ? remaining : TimeSpan.Zero;
        }
        return null;
    }

    private FetchResult Parse(string body)
    {
        try
        {
            using var doc = JsonDocument.Parse(body);
            if (!doc.RootElement.TryGetProperty("limits", out var limits)
                || limits.ValueKind != JsonValueKind.Array)
                return Fail(FetchErrorKind.Malformed, "В ответе нет массива limits.");

            Limit? session = null, weeklyAll = null, fable = null;
            foreach (var entry in limits.EnumerateArray())
            {
                var kind = entry.TryGetProperty("kind", out var k) ? k.GetString() : null;
                switch (kind)
                {
                    case "session":
                        session = MapLimit(entry, LimitKind.Session, "5-hour");
                        break;
                    case "weekly_all":
                        weeklyAll = MapLimit(entry, LimitKind.WeeklyAll, "Weekly");
                        break;
                    case "weekly_scoped" when IsFable(entry):
                        fable = MapLimit(entry, LimitKind.WeeklyScoped, "Fable 5");
                        break;
                }
            }

            if (session is null || weeklyAll is null || fable is null)
                return Fail(FetchErrorKind.Malformed, "В ответе отсутствует один из трёх лимитов.");

            var snapshot = new UsageSnapshot(new[] { session, weeklyAll, fable }, _time.GetUtcNow());
            return new FetchResult.Success(snapshot);
        }
        catch (Exception ex) when (ex is JsonException or FormatException
            or InvalidOperationException or KeyNotFoundException)
        {
            return Fail(FetchErrorKind.Malformed, $"Некорректный ответ Claude Code: {ex.Message}");
        }
    }

    private static Limit MapLimit(JsonElement entry, LimitKind kind, string displayName)
    {
        var percent = entry.GetProperty("percent").GetDouble();
        var headroom = Math.Clamp(100 - percent, 0, 100);
        var resetsAt = entry.GetProperty("resets_at").GetDateTimeOffset();
        var isActive = entry.TryGetProperty("is_active", out var active)
            && active.ValueKind == JsonValueKind.True;
        return new Limit(kind, displayName, headroom, resetsAt, isActive);
    }

    private static bool IsFable(JsonElement entry)
        => entry.TryGetProperty("scope", out var scope)
            && scope.ValueKind == JsonValueKind.Object
            && scope.TryGetProperty("model", out var model)
            && model.ValueKind == JsonValueKind.Object
            && model.TryGetProperty("display_name", out var name)
            && name.ValueKind == JsonValueKind.String
            && string.Equals(name.GetString(), "Fable", StringComparison.OrdinalIgnoreCase);

    private static FetchResult Fail(FetchErrorKind kind, string message, TimeSpan? retryAfter = null)
        => new FetchResult.Failure(new FetchError(kind, message, retryAfter));
}
