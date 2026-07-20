# Решение: форма провайдерного порта в Core (.NET-интерфейс)

Ассет к тикету #6. Форма провайдерного порта и доменных типов в библиотеке
`Core` (.NET/C#) под расширяемость: какой контракт реализует каждый провайдер,
чтобы трей-виджет (и будущее мобильное приложение — отдельный эффорт) работали
с любым провайдером единообразно. Claude Code — первая и единственная в MVP
реализация.

Опирается на: стек .NET + Avalonia с UI-агностичным `Core` (#4), форму источника
Claude Code (#2), модель обновления (#5).

## TL;DR

`Core` знает ровно один контракт — `IUsageProvider` (статичная идентичность +
асинхронный `FetchAsync`). Fetch возвращает **результат-конверт** `FetchResult =
Success(снимок) | Failure(причина)` — штатные сбои это данные, не исключения.
Снимок несёт доменные `Limit` (headroom, не percent) + `FetchedAt`. Добыча
токена вынесена за инъектируемый `ICredentialSource`, живущий **рядом с адаптером
Claude Code**, а не в `Core`. Кэш, опрос, backoff, пороги/цвета — вне порта
(слой обновления #5 и слой представления #3).

## Доменные типы и порт в `Core`

```csharp
namespace WidgetSubscription.Core;

// Единственный контракт, который знает Core. Про токены/файлы/OAuth не знает.
public interface IUsageProvider
{
    ProviderInfo Info { get; }                     // статика, без сети
    Task<FetchResult> FetchAsync(CancellationToken ct);
}

// Идентичность провайдера — статична, доступна даже при Failure (серый донат).
public sealed record ProviderInfo(
    string Id,            // "claude-code" — стабильный ключ (кэш, настройки, выбор)
    string DisplayName,   // "Claude Code"
    string BrandColor);   // hex бренда провайдера (не цвет состояния)

// Результат-конверт: штатные сбои — данные, не исключения.
public abstract record FetchResult
{
    public sealed record Success(UsageSnapshot Snapshot) : FetchResult;
    public sealed record Failure(FetchError Error) : FetchResult;
    private FetchResult() { }                       // закрытая иерархия
}

public sealed record UsageSnapshot(
    IReadOnlyList<Limit> Limits,
    DateTimeOffset FetchedAt);                       // ставит порт в момент ответа

public sealed record Limit(
    LimitKind Kind,
    string DisplayName,        // «5-часовой» / «Недельный» / «Fable 5» — из адаптера
    double Headroom,           // 0..100, уже 100−percent
    DateTimeOffset ResetsAt,   // абсолютный; «через N» считает UI
    bool IsActive);            // связывающий сейчас лимит

public enum LimitKind { Session, WeeklyAll, WeeklyScoped }

public sealed record FetchError(
    FetchErrorKind Kind,       // исчерпанный исход, а не каждая внутренняя попытка
    string Message,            // человекочитаемо, для панели
    TimeSpan? RetryAfter = null); // из Retry-After; backoff считает слой обновления

public enum FetchErrorKind { NoCredentials, Unauthorized, SourceUnavailable, Timeout, Malformed }
```

## Адаптер Claude Code (отдельная сборка, не `Core`)

```csharp
namespace WidgetSubscription.Providers.ClaudeCode;

public sealed class ClaudeCodeAdapter : IUsageProvider
{
    private readonly ICredentialSource _credentials;
    private readonly HttpClient _http;

    public ClaudeCodeAdapter(ICredentialSource credentials, HttpClient http)
    {
        _credentials = credentials;
        _http = http;
    }

    // Бренд-цвет Claude — терракота; точное значение уточнить при реализации.
    public ProviderInfo Info => new("claude-code", "Claude Code", "#D97757");

    public async Task<FetchResult> FetchAsync(CancellationToken ct)
    {
        // 1. _credentials.GetAsync(ct); null => Failure(NoCredentials).
        // 2. GET https://api.anthropic.com/api/oauth/usage с заголовками
        //    Authorization: Bearer <token>, anthropic-beta: oauth-2025-04-20,
        //    anthropic-version: 2023-06-01.
        // 3. На 401 — снова _credentials.GetAsync (перечитать) + 1 ретрай;
        //    не помогло => Failure(Unauthorized).
        // 4. Разобрать limits[] -> три доменных Limit (см. #2), headroom = 100−percent.
        //    Успех => Success(new UsageSnapshot(limits, DateTimeOffset.Now)).
        // 5. Прочие исходы -> Failure(SourceUnavailable | Timeout | Malformed),
        //    RetryAfter из заголовка Retry-After если есть.
    }
}

// Решение B: добыча токена вынесена и инъектируется, но живёт рядом с адаптером,
// а не портом в Core. Другой провайдер заведёт свою абстракцию под свою
// аутентификацию; спекулятивный общий auth-порт в Core не поднимаем, пока
// провайдер один.
public interface ICredentialSource
{
    Task<AccessToken?> GetAsync(CancellationToken ct);  // null => креды недоступны
}

public sealed record AccessToken(string Value, DateTimeOffset? ExpiresAt);

// Реализация MVP: читает ~/.claude/.credentials.json, read-only (#5) — не пишет файл.
public sealed class ClaudeCredentialsFileSource : ICredentialSource
{
    // Путь ~/.claude/.credentials.json — константа адаптера; инъекция пути в MVP не нужна.
}
```

## Решения по веткам (грилинг #6)

1. **Недоступность источника — результат-тип, не исключения.** `FetchResult =
   Success | Failure`. #5 сделал сбои штатным потоком (401 / нет файла /
   протухший снимок рисуются как состояния панели), значит порт отдаёт сбой как
   данные. Настоящие исключения (баг, отмена по `CancellationToken`) — пусть летят.
2. **Доменный `Limit`.** `Kind` — **enum** (`Session`/`WeeklyAll`/`WeeklyScoped`)
   для исчерпывающего `switch` в UI; **`Headroom`** (0..100 = 100−percent), не
   percent — доменный термин из глоссария. `severity` не тащим (пороги/цвета —
   решение #3, из headroom в слое представления); отдельное поле `Scope` не
   заводим — Fable выражается `Kind = WeeklyScoped` + `DisplayName = "Fable 5"`.
3. **Конверт снимка и стык с #5.** `Success(UsageSnapshot{Limits, FetchedAt})` /
   `Failure(FetchError{Kind, Message, RetryAfter?})`. `FetchedAt` ставит **порт**
   (знает момент реального ответа — иначе «as of» врёт). Кэш, «возраст >90 с»,
   антидребезг, таймер 60 с, backoff 60→300 с — **целиком слой обновления**; порт
   лишь прокидывает `RetryAfter` как факт. 401-ретрай инкапсулирован в адаптере —
   наружу выходит `Unauthorized` только если ретрай не помог.
4. **Идентичность провайдера — статическое свойство порта.** `ProviderInfo{Id,
   DisplayName, BrandColor}`, отдельно от динамического снимка: UI рисует
   провайдера даже при `Failure`. `Id` — стабильный строковый ключ (не enum:
   провайдеры — точка расширения). `BrandColor` — цвет бренда, не состояния.
   Иконку провайдера в MVP не заводим (один провайдер); добавится тем же
   способом при втором.
5. **Добыча токена — вынесена и инъектируется (решение B).**
   `ICredentialSource` живёт рядом с адаптером Claude Code, не портом в `Core`.
   `IUsageProvider` не упоминает токены/файлы/OAuth. Даёт тестируемость (подмена
   источника фейком без HTTP) и шов под мобильное/другие провайдеры (иной
   источник за тем же интерфейсом). Общий auth-порт в `Core` не поднимаем, пока
   нечего обобщать.
