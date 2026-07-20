# Спека MVP — трей-виджет лимитов подписки Claude Code

Согласованный, готовый к передаче артефакт карты #1 (wayfinder). Сводит пять
закрытых решений в единый документ. **Ничего нового здесь не решается** — это
схождение уже принятого; деталь каждого решения живёт в своём тикете и ассете,
сюда вынесены суть и связки.

Статус: назначение карты. После согласования этого документа открытых решений на
карте нет — в тумане остаётся лишь условный opt-in self-refresh токена (см.
[§11](#11-вне-области-mvp-и-туман)).

---

## 1. Назначение

Утилита в области уведомлений Windows (системный трей), показывающая, сколько
осталось (**headroom**) по трём лимитам подписки Claude Code и когда каждый
**сбрасывается**. Иконка кодирует состояние «с одного взгляда»; клик раскрывает
панель с тремя лимитами подробно.

MVP — **только Claude Code / Anthropic**. Мультипровайдерность заложена в дизайн
как расширяемость (провайдерный порт), но реальных интеграций других провайдеров
в MVP нет. Стандинг-требования эффорта: **кросс-платформенность** (Windows
сейчас; macOS/Linux — задел, не реализуем) и **общее ядро с будущим мобильным
приложением** (отдельный эффорт, см. [§11](#11-вне-области-mvp-и-туман)).

Глоссарий домена — `CONTEXT.md` в корне (Provider, Limit, Fable 5, Headroom,
Reset, Tray widget). Ключевое: **Headroom = 100 − использовано (%)**; **Reset** —
момент обновления окна лимита.

## 2. Три лимита

| Лимит                    | Окно                     | Запись источника (`limits[]`)                  |
|--------------------------|--------------------------|------------------------------------------------|
| 5-часовой (session)      | скользящее 5 ч           | `kind: session`                                |
| Недельный общий          | фиксированная неделя     | `kind: weekly_all`                             |
| Недельный Fable 5        | фиксированная неделя     | `kind: weekly_scoped`, `scope.model = "Fable"` |

По каждому виджет показывает headroom (%) и время до сброса.

## 3. Источник данных (тикет #2)

Деталь: [тикет #2](https://github.com/VladPatr96/widget-subscription/issues/2),
ассет `docs/research/ticket-2-data-source.md`.

- **Эндпоинт**: `GET https://api.anthropic.com/api/oauth/usage` (проверено вживую,
  HTTP 200). Официальный OAuth-эндпоинт Claude Code, не публичный API.
- **Заголовки**: `Authorization: Bearer <accessToken>`,
  `anthropic-beta: oauth-2025-04-20`, `anthropic-version: 2023-06-01`.
- **Токен**: `accessToken` из `~/.claude/.credentials.json`
  (`claudeAiOauth.accessToken`). Переиспользуем токен Claude Code — **без
  браузерного скрейпа** claude.ai (guardrail MVP соблюдён).
- **Ответ**: массив `limits[]` даёт все три лимита; по каждому — `percent`
  (использовано), `resets_at` (**абсолютный** ISO-8601 со смещением), `is_active`
  (связывающий сейчас лимит). **Headroom = 100 − percent.** Обратный отсчёт до
  сброса считает виджет как `resets_at − now`.
- **Отклонено**: локальные файлы `~/.claude` (только расход в токенах, не
  headroom/reset); statusline `rate_limits` (лишь 2 из 3 окон, только пока Claude
  Code запущен); заголовки inference-API (единый показатель, нет разбивки).

## 4. Архитектура и стек (тикет #4)

Деталь: [тикет #4](https://github.com/VladPatr96/widget-subscription/issues/4).

- **Стек**: **.NET + Avalonia UI**. Выбран под кросс-платформу и общее ядро с
  будущим мобильным приложением.
- **Разбиение**:
  - `Core` — UI-агностичное ядро: домен + провайдерный порт + fetch/compute.
    Не знает про UI, токены, файлы, OAuth.
  - `Providers.ClaudeCode` — адаптер провайдера: HTTP-вызов эндпоинта, разбор
    ответа, добыча токена. Отдельная сборка, не `Core`.
  - UI-слой (Avalonia) — трей-иконка + панель.
- **Зависимости**: динамическая донат-иконка — **SkiaSharp**; HTTP —
  `HttpClient`; JSON — `System.Text.Json` (стдлиб). Нулевые внешние зависимости
  сверх Avalonia/SkiaSharp.
- **Отклонено**: WinForms/WPF (Windows-only), Rust+tray-icon (незрелый мобильный
  UI), Python+pystray, Flutter (слабый нативный трей).

## 5. Провайдерный порт и доменные типы (тикет #6)

Деталь: [тикет #6](https://github.com/VladPatr96/widget-subscription/issues/6),
ассет `docs/design/ticket-6-provider-port.md`.

`Core` знает **ровно один контракт** — `IUsageProvider`. Штатные сбои — **данные,
не исключения** (результат-конверт).

```csharp
namespace WidgetSubscription.Core;

public interface IUsageProvider
{
    ProviderInfo Info { get; }                     // статика, без сети
    Task<FetchResult> FetchAsync(CancellationToken ct);
}

public sealed record ProviderInfo(string Id, string DisplayName, string BrandColor);

public abstract record FetchResult
{
    public sealed record Success(UsageSnapshot Snapshot) : FetchResult;
    public sealed record Failure(FetchError Error) : FetchResult;
    private FetchResult() { }
}

public sealed record UsageSnapshot(IReadOnlyList<Limit> Limits, DateTimeOffset FetchedAt);

public sealed record Limit(
    LimitKind Kind,
    string DisplayName,        // «5-часовой» / «Недельный» / «Fable 5»
    double Headroom,           // 0..100, уже 100−percent
    DateTimeOffset ResetsAt,   // абсолютный; «через N» считает UI
    bool IsActive);

public enum LimitKind { Session, WeeklyAll, WeeklyScoped }

public sealed record FetchError(FetchErrorKind Kind, string Message, TimeSpan? RetryAfter = null);

public enum FetchErrorKind { NoCredentials, Unauthorized, SourceUnavailable, Timeout, Malformed }
```

Ключевые решения формы:
- `Limit` несёт **Headroom** (0..100), не percent — доменный термин. `severity` не
  тащим (пороги/цвета — слой представления, из headroom). Отдельного поля `Scope`
  нет — Fable выражается `Kind = WeeklyScoped` + `DisplayName = "Fable 5"`.
- `FetchedAt` ставит **порт** в момент реального ответа.
- `ProviderInfo` статичен и доступен даже при `Failure` (провайдера рисуем всегда,
  напр. серый донат). `Id` — стабильный строковый ключ (не enum: провайдеры —
  точка расширения). `BrandColor` — цвет бренда, не состояния.
- **Добыча токена — за инъектируемым `ICredentialSource`** (решение B), живущим
  **рядом с адаптером** Claude Code, а не портом в `Core`. Даёт тестируемость
  (фейк без HTTP) и шов под мобильное/другие провайдеры. Общий auth-порт в `Core`
  не поднимаем, пока провайдер один.
- 401-ретрай инкапсулирован в адаптере: наружу выходит `Unauthorized` только если
  ретрай не помог.

```csharp
namespace WidgetSubscription.Providers.ClaudeCode;

public interface ICredentialSource
{
    Task<AccessToken?> GetAsync(CancellationToken ct);  // null => креды недоступны
}
public sealed record AccessToken(string Value, DateTimeOffset? ExpiresAt);

// MVP: ClaudeCredentialsFileSource читает ~/.claude/.credentials.json, read-only.
// Бренд-цвет Claude — терракота (#D97757, уточнить при реализации).
```

## 6. Модель обновления (тикет #5)

Деталь: [тикет #5](https://github.com/VladPatr96/widget-subscription/issues/5).

- **Опрос** каждые **60 с** + **форс при раскрытии панели**, если данным >30 с
  (антидребезг).
- **Кэш** = последний снимок в памяти + `FetchedAt`; **без персиста** на диск.
  Возраст снимка показываем в панели при **>90 с**. Обратный отсчёт до сброса
  всегда точен (абсолютный `resets_at`, не зависит от возраста снимка).
- **Токен read-only**: виджет **только читает** `.credentials.json`, сам **не
  рефрешит и не пишет** файл. На 401 — перечитать файл + 1 ретрай → иначе
  деградация. (Причина: ротирующийся refresh-токен с reuse-detection; self-refresh
  рискует отозвать семью токенов и разлогинить Claude Code.)
- **Ошибки**: backoff **60 → 300 с**, уважаем заголовок `Retry-After`.

## 7. Визуальный язык (тикет #3)

Деталь: [тикет #3](https://github.com/VladPatr96/widget-subscription/issues/3),
ассет `docs/prototypes/ticket-3-visual-language.html`.

- **Трей-иконка** = **донат**: дуга кодирует **худший (наименьший) headroom** из
  трёх лимитов; цвет — по порогам.
- **Пороги**: ок **>30%** / близко **10–30%** / критично **<10%** / исчерпан
  **0%**.
- **Панель** (раскрытие) = **три бара**: 5-часовой / недельный общий / Fable 5.
  Каждый бар: имя лимита, **headroom %**, бейдж состояния, время сброса.
- **Сброс**: относительное «через N» + абсолютное время подсказкой.
- **Исчерпан**: 0% + бейдж + «сброс через N».

## 8. Деградация и ошибки

Сбои — штатные состояния UI, не крэши (см. `FetchResult.Failure`):

- **Серый донат** + причина текстом в панели.
- Причины: старый снимок (данные >порога), нет данных (`SourceUnavailable` /
  `Timeout` / `Malformed`), нет `.credentials.json` (`NoCredentials`),
  не авторизован после ретрая (`Unauthorized`).
- Идентичность провайдера (`ProviderInfo`) рисуется даже при полном отказе fetch.

## 9. Расширяемость (заложено, не реализуем в MVP)

- **Другие провайдеры**: заводят свою реализацию `IUsageProvider` + свою
  абстракцию аутентификации рядом со своим адаптером. `Core` не меняется.
- **Мобильное приложение**: общее ядро на .NET — задел; само приложение и его
  источник данных — отдельный эффорт (на телефоне нет локального токена Claude
  Code).
- Иконку провайдера в MVP не заводим (один провайдер); добавится тем же способом
  при втором.

## 10. Границы MVP (checklist приёмки)

MVP считается готовым, когда:
1. Виджет читает три лимита с эндпоинта, используя токен из `.credentials.json`.
2. Трей-иконка (донат) отражает худший headroom и порог цветом.
3. Панель показывает три бара с headroom %, бейджем, временем сброса.
4. Опрос 60 с + форс при раскрытии; кэш в памяти; возраст при >90 с.
5. Токен read-only; 401 → перечитать + ретрай → деградация.
6. Все сбои деградируют в серый донат + текст причины, без крэшей.
7. `Core` UI-агностичен; адаптер Claude Code — отдельная сборка за
   `IUsageProvider` / `ICredentialSource`.

## 11. Вне области MVP и туман

**Out of scope** (не выходит из тумана без перечерчивания назначения):
- Реальные интеграции других провайдеров (OpenAI, Gemini, Grok…).
- Браузерный скрейп веб-кабинета claude.ai.
- Упаковка / инсталлятор / автозапуск / дистрибуция.
- Мобильное приложение (iOS/Android) и его источник данных.

**В тумане (условный, после MVP)**:
- **Самостоятельный рефреш токена (opt-in)** — если read-only окажется мало
  (Claude Code часто не запущен → устаревшие данные), реализовать безопасный
  self-refresh (`POST https://platform.claude.com/v1/oauth/token`, атомарная
  перезапись `.credentials.json`, с учётом ротации и reuse-detection). Выйдет в
  тикет, только если проверка вживую покажет необходимость.

---

## Источники решений

| Тикет | Решение | Ассет |
|-------|---------|-------|
| [#2](https://github.com/VladPatr96/widget-subscription/issues/2) | Источник данных | `docs/research/ticket-2-data-source.md` |
| [#3](https://github.com/VladPatr96/widget-subscription/issues/3) | Визуальный язык | `docs/prototypes/ticket-3-visual-language.html` |
| [#4](https://github.com/VladPatr96/widget-subscription/issues/4) | Стек реализации | — |
| [#5](https://github.com/VladPatr96/widget-subscription/issues/5) | Модель обновления | — |
| [#6](https://github.com/VladPatr96/widget-subscription/issues/6) | Провайдерный порт | `docs/design/ticket-6-provider-port.md` |
