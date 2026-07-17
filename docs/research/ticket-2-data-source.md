# Разведка: источник данных лимитов Claude Code

Ассет к тикету #2. Как программно читать остаток (headroom) и время до сброса
по трём лимитам Claude Code — 5-часовой скользящий (session), недельный общий
(weekly_all), недельный на Fable 5 (weekly_scoped) — для трей-виджета MVP.

Проверено на реальной машине разработчика: Claude Code v2.1.211 (native),
подписка `max`, тариф `default_claude_max_5x`, Windows. Секреты (OAuth-токены,
Authorization) вычищены — приведены только формы полей.

## TL;DR — рекомендация для MVP

**Источник (c): официальный эндпоинт `GET https://api.anthropic.com/api/oauth/usage`,
с переиспользованием OAuth-токена из `~/.claude/.credentials.json`.**

Единственный источник, отдающий **все три** лимита одним чистым JSON-вызовом,
с процентом использования и **абсолютным** временем сброса по каждому. Проверено
живым вызовом — HTTP 200, данные ниже. `a` даёт только расход в токенах (не
headroom), `b` (statusline) — только 2 из 3 окон и лишь пока Claude Code запущен.

## Что реально доступно по источникам

### (a) Локальные файлы под `~/.claude/` — ОТКЛОНЁН для headroom/reset

- `stats-cache.json` — учёт **расхода в токенах** в стиле `ccusage` (агрегируется
  из JSONL-транскриптов). Поля: `modelUsage.<model>.{inputTokens, outputTokens,
  cacheReadInputTokens, cacheCreationInputTokens, webSearchRequests, costUSD}`,
  `dailyActivity`, `totalSessions`, `totalMessages`. **Нет headroom, нет reset,
  нет размеров лимитов.** `costUSD: 0` на Max-плане. `lastComputedDate` устаревает
  (считается по запросу, не в реальном времени). Превратить токены в headroom
  можно только зная неопубликованные размеры лимитов Anthropic и алгоритм
  скользящего 5-часового окна — хрупкая аппроксимация. Недельный лимит Fable 5
  отсюда не выводится.
- `.credentials.json` — **не данные о лимитах**, но ключ к источнику (c). Форма:
  `{ claudeAiOauth: { accessToken: "sk-ant-oat01-…", refreshToken: "sk-ant-ort01-…",
  expiresAt: <ms>, refreshTokenExpiresAt: <ms>, scopes: [… "user:inference" …],
  subscriptionType: "max", rateLimitTier: "default_claude_max_5x" } }`.
- Живого `rate_limits` ни в одном локальном файле нет — он либо пушится в
  statusline, либо тянется с эндпоинта.

Сырой пример (`stats-cache.json`, расход, НЕ лимиты):

```json
"modelUsage": {
  "claude-fable-5":  { "inputTokens": 957575, "outputTokens": 3637064,
                       "cacheReadInputTokens": 439057697, "costUSD": 0 },
  "claude-opus-4-8": { "inputTokens": 527536, "outputTokens": 1778805,
                       "cacheReadInputTokens": 203826144, "costUSD": 0 }
}
```

### (b) `/usage` / statusline `rate_limits` — ЧАСТИЧНО

- Команда `/usage` — интерактивный TUI-диалог (бары по всем лимитам), **не
  скриптуемый stdout**. Данные берёт с того же эндпоинта (c) (по changelog:
  «usage endpoint rate-limited», «refreshes OAuth token»).
- Statusline-пейлоад (changelog v-строка: *«Added `rate_limits` field to statusline
  scripts … 5-hour and 7-day windows with `used_percentage` and `resets_at`»*):
  Claude Code пушит JSON на stdin statusline-скрипта. Форма:
  `.rate_limits.five_hour.{used_percentage, resets_at}` и
  `.rate_limits.seven_day.{used_percentage, resets_at}`.
- Ограничения: **только 2 из 3 окон** (5h + недельный общий; **Fable 5 нет**);
  приходит **только пока Claude Code запущен и рендерит** статуслайн. Для
  автономного трея пришлось бы держать фейковый statusline внутри живой сессии CC.
  Годится как вторичный сигнал, не как основной.

### (c) Официальный эндпоинт — РЕКОМЕНДОВАН

- **`GET https://api.anthropic.com/api/oauth/usage`**
- Заголовки: `Authorization: Bearer <accessToken из ~/.claude/.credentials.json>`,
  `anthropic-beta: oauth-2025-04-20`, `anthropic-version: 2023-06-01`.
- Проверено: **HTTP 200**, `content-type: application/json`.
- Массив `limits[]` — по записи на лимит, ровно три нужных:
  - `kind: "session"` — 5-часовой скользящий,
  - `kind: "weekly_all"` — недельный общий,
  - `kind: "weekly_scoped"`, `scope.model.display_name: "Fable"` — недельный Fable 5.
- Поля лимита: `percent` (использовано, %), `resets_at` (**абсолютный** ISO-8601 со
  смещением TZ), `severity` (`normal` | …), `is_active` (какой лимит сейчас
  связывающий). **Headroom = 100 − percent.** Есть и легаси-объекты `five_hour` /
  `seven_day` с `utilization` + `resets_at`, и `extra_usage` (кредиты).

Endpoint-URL и имена заголовков извлечены из бинаря `claude.exe` v2.1.211
(`/api/oauth/usage`, `anthropic-ratelimit-unified-*`).

Сырой ответ эндпоинта (реальный, секретов нет):

```json
{
  "five_hour": { "utilization": 15.0, "resets_at": "2026-07-17T14:59:59.625643+00:00",
                 "limit_dollars": null, "used_dollars": null, "remaining_dollars": null },
  "seven_day": { "utilization": 2.0,  "resets_at": "2026-07-24T06:59:59.625670+00:00",
                 "limit_dollars": null, "used_dollars": null, "remaining_dollars": null },
  "extra_usage": { "is_enabled": false, "monthly_limit": null, "used_credits": null,
                   "utilization": null, "currency": null },
  "limits": [
    { "kind": "session",       "group": "session", "percent": 15, "severity": "normal",
      "resets_at": "2026-07-17T14:59:59.625643+00:00", "scope": null, "is_active": true },
    { "kind": "weekly_all",    "group": "weekly",  "percent": 2,  "severity": "normal",
      "resets_at": "2026-07-24T06:59:59.625670+00:00", "scope": null, "is_active": false },
    { "kind": "weekly_scoped", "group": "weekly",  "percent": 2,  "severity": "normal",
      "resets_at": "2026-07-24T06:59:59.626085+00:00",
      "scope": { "model": { "id": null, "display_name": "Fable" }, "surface": null },
      "is_active": false }
  ],
  "spend": { "used": { "amount_minor": 0, "currency": "USD", "exponent": 2 },
             "percent": 0, "enabled": false }
}
```

Раскладка трёх лимитов назначения из этого ответа:

| Лимит назначения        | Запись `limits[]`                              | Использовано | Headroom | Сброс (абс.)                 |
|-------------------------|------------------------------------------------|--------------|----------|------------------------------|
| 5-часовой скользящий    | `kind: session`                                | 15%          | 85%      | 2026-07-17T14:59:59Z         |
| Недельный общий         | `kind: weekly_all`                             | 2%           | 98%      | 2026-07-24T06:59:59Z         |
| Недельный Fable 5       | `kind: weekly_scoped`, scope.model = "Fable"   | 2%           | 98%      | 2026-07-24T06:59:59Z         |

### Фолбэк-фолбэк: заголовки inference-API

На ответах messages/inference API приходят `anthropic-ratelimit-unified-*`
(`-status`, `-reset`, `-fallback`, `-overage-*`, `-upgrade-paths`). Это **единый**
показатель, требует сделать inference-вызов и не даёт разбивку по трём лимитам.
Не рекомендуется.

## Семантика данных

- **Единицы**: `percent` / `utilization` — процент **использования** (0–100).
  Headroom = 100 − используется. `*_dollars` = null на этом плане (проценты, не $).
- **Время**: `resets_at` — **абсолютная** метка ISO-8601 со смещением (`+00:00`).
  Не относительная — «время до сброса» виджет считает сам как `resets_at − now`.
  5h окно скользит (сброс сдвигается), недельные — фиксированные даты.
- **Активность**: `is_active` помечает связывающий лимит; `severity` эскалирует
  при приближении к пределу (наблюдалось `normal`).

## Надёжность и хрупкость

| Источник | Все 3 лимита | Reset | Headroom | Работает без CC | Хрупкость |
|----------|:---:|:---:|:---:|:---:|-----------|
| (a) файлы `~/.claude` | нет | нет | нет (только токены) | да | нужны неопубл. размеры лимитов + алгоритм окна |
| (b) statusline `rate_limits` | нет (2/3) | да | да | **нет** (нужен живой CC) | зависит от рендера statusline |
| (c) `oauth/usage` | **да** | **да** | **да** | **да** | OAuth-токен истекает (~часы) → нужен рефреш; неопубл. эндпоинт может закрыться/загейтиться |

Хрупкость (c) и как её снять:
1. **Истечение токена.** `accessToken` живёт часы (`expiresAt`). Виджет должен
   повторять рефреш Claude Code: `POST https://platform.claude.com/v1/oauth/token`
   с `refreshToken`, и переписывать `~/.claude/.credentials.json` (или читать его
   заново — CC сам рефрешит на своих вызовах). MVP: читать файл перед каждым
   опросом, при 401 — рефрешить.
2. **Неофициальность.** `/api/oauth/usage` — внутренний OAuth-эндпоинт CC, не
   публичный API. Может измениться. Митигейт: инкапсулировать за портом провайдера
   (см. туман «Провайдерная абстракция»), вторичный сигнал — statusline (b).
3. Guardrail MVP соблюдён: **без браузерного скрейпа** claude.ai — только
   переиспользование токена CC на его же эндпоинте.
