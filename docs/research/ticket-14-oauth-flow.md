# Разведка: OAuth-флоу Claude Code для собственного логина виджета

Ассет к [тикету #14](https://github.com/VladPatr96/widget-subscription/issues/14).
Как Claude Code получает свой OAuth-токен, чтобы виджет мог провести **собственный
логин** на чистой машине (режим «own login» из карты #13) — и как это соотносится
с уже используемым read-only заимствованием токена Claude Code (режим «borrow»).

**Тип: research (AFK).** Источники — официальный эндпоинт (проверен вживую в
рамках MVP, тикет #2), официальная документация Anthropic по OAuth-коннекторам и
несколько независимых реверс-реализаций флоу Claude Code. Флоу **не публичный API**:
это внутренний OAuth Claude Code, детали получены реверсом и могут меняться. Секреты
не приводятся — только формы полей.

## TL;DR

Claude Code — **публичный OAuth 2.0-клиент (Authorization Code + PKCE, без
client_secret)**. Виджет может воспроизвести тот же флоу и получить **собственный
набор токенов** (свою семью refresh-токенов), не пересекающийся с грантом Claude
Code, **при условии** что виджет хранит свои токены **в собственном месте**, а не
перезаписывает `~/.claude/.credentials.json`.

- **client_id** (публичный, у всех тулов один): `9d1c250a-e61b-44d9-88ed-5944d1962f5e`.
- **PKCE**: `S256`, `code_challenge` = base64url(SHA-256(`code_verifier`)) без паддинга.
- **Authorize**: `https://claude.ai/oauth/authorize` (вход по **подписке** Pro/Max) —
  именно этот грант даёт `subscriptionType: max` и работает с
  `GET api.anthropic.com/api/oauth/usage`. Альтернатива `https://console.anthropic.com/oauth/authorize`
  — вход по **Console/API-аккаунту** (не то, что нужно виджету лимитов подписки).
- **Token**: `POST https://console.anthropic.com/v1/oauth/token`
  (не `platform.claude.com/...`, как значилось в тумане MVP — **уточнено**).
- **Scope**: `org:create_api_key user:profile user:inference`; минимально для чтения
  usage достаточно `user:inference` (он и присутствует в кредах Claude Code).
- **Redirect**: два рабочих способа (см. §3) — **loopback на localhost** (лучший UX
  для GUI-виджета) или **hosted callback + ручная вставка кода** (фолбэк для headless).
- **Access-токен живёт ~8 ч** (`expires_in: 28800`), **refresh ротируется**
  (каждый успешный refresh возвращает **новый** refresh-токен — старый инвалидируется;
  повторное использование старого = reuse-detection).

## 1. Тип клиента и client_id

Claude Code — **public client**: нет client_secret, безопасность обмена держится на
PKCE. `client_id = 9d1c250a-e61b-44d9-88ed-5944d1962f5e` — один и тот же
захардкоженный публичный идентификатор у самого Claude Code и у всех сторонних
реализаций флоу. Виджет использует его же (своего OAuth-приложения Anthropic не
регистрируют; регистрация клиентов под этот флоу закрыта).

Следствие для разделения грантов (см. §6): один client_id у CC и виджета —
не проблема, пока это **разные авторизационные гранты** (разные прогоны PKCE →
разные семьи refresh-токенов), а виджет не трогает файл CC.

## 2. PKCE и запрос авторизации

1. Сгенерировать 32 криптослучайных байта → base64url без `=` → это `code_verifier`
   (обычно 43-символьная строка).
2. `code_challenge` = base64url(SHA-256(`code_verifier`)) без `=`.
3. `state` = то же случайное значение, что и `code_verifier` (наблюдаемая практика
   этого флоу; сервер возвращает `code` и опционально `state`).

Параметры authorize-запроса (`claude.ai/oauth/authorize`):

| Параметр                | Значение                                            |
|-------------------------|-----------------------------------------------------|
| `client_id`             | `9d1c250a-e61b-44d9-88ed-5944d1962f5e`              |
| `response_type`         | `code`                                              |
| `redirect_uri`          | зависит от способа (см. §3)                          |
| `scope`                 | `org:create_api_key user:profile user:inference`    |
| `code_challenge`        | base64url(SHA-256(verifier))                        |
| `code_challenge_method` | `S256`                                              |
| `state`                 | случайное (= `code_verifier`)                        |
| `code`                  | `true` — только в hosted-paste режиме (см. §3)       |

## 3. Способ redirect — что реалистично для десктоп-виджета

Два подтверждённых способа:

### (a) Loopback на localhost (RFC 8252, «native app») — РЕКОМЕНДОВАН для GUI

Реальный интерактивный `claude login` слушает **эфемерный порт localhost** и
редиректит браузер туда:
- `redirect_uri = http://localhost:<random-port>/callback` (порт меняется от сессии
  к сессии; клиент регистрирует loopback `http://localhost/callback` и
  `http://127.0.0.1/callback`, компонент порта игнорируется по RFC 8252).
- Виджет поднимает временный HTTP-listener на свободном порту, открывает браузер на
  authorize-URL, ловит `code` в редиректе, гасит listener, меняет код на токены.
- **UX**: без ручной вставки — пользователь только логинится/подтверждает в браузере.
- **Риски**: сбоит в WSL/Docker/SSH/удалённых сессиях, где браузер не достучится до
  localhost; иногда браузер-специфично (Chrome стабильнее). Для локального
  Windows-виджета — приемлемо.

### (b) Hosted callback + ручная вставка кода — ФОЛБЭК (headless)

- `redirect_uri = https://console.anthropic.com/oauth/code/callback` + `code=true`.
- После подтверждения Anthropic показывает код (иногда в форме `CODE#STATE`);
  пользователь копирует его обратно в виджет.
- Не нужен локальный сервер; работает в headless/удалённых окружениях.
- **UX хуже** (ручной copy-paste), но нулевая зависимость от сетевой доступности
  localhost.

**Рекомендация для виджета**: основной путь — (a) loopback; (b) — резервная кнопка
«вставить код вручную», если listener не поднялся/браузер не достучался.

## 4. Обмен кода на токены

`POST https://console.anthropic.com/v1/oauth/token`
Заголовки: `Content-Type: application/json`, `User-Agent: anthropic`.
Тело (JSON):

```json
{
  "grant_type": "authorization_code",
  "code": "<код из редиректа>",
  "code_verifier": "<исходный verifier>",
  "client_id": "9d1c250a-e61b-44d9-88ed-5944d1962f5e",
  "redirect_uri": "<тот же, что в authorize>",
  "state": "<если вернулся в редиректе>"
}
```

Ответ (форма; секреты вычищены):

```json
{
  "token_type": "Bearer",
  "access_token": "sk-ant-oat01-…",
  "refresh_token": "sk-ant-ort01-…",
  "expires_in": 28800,
  "scope": "user:inference user:profile",
  "organization": { "uuid": "…", "name": "…" },
  "account": { "uuid": "…", "email_address": "…" }
}
```

- `access_token` — префикс `sk-ant-oat01-`, живёт **~8 ч** (`expires_in: 28800`).
  Совпадает с формой `accessToken` из `~/.claude/.credentials.json` (тикет #2).
- `refresh_token` — префикс `sk-ant-ort01-`, долгоживущий (в кредах CC есть отдельный
  `refreshTokenExpiresAt`).
- `expires_in` — секунды; `expiresAt` виджет считает как `now + expires_in`.

## 5. Refresh и ротация (ключевое для безопасности)

`POST https://console.anthropic.com/v1/oauth/token`, тело:

```json
{
  "grant_type": "refresh_token",
  "refresh_token": "<текущий refresh>",
  "client_id": "9d1c250a-e61b-44d9-88ed-5944d1962f5e"
}
```

- Ответ той же формы: **новый** `access_token`, **новый** `expires_in` и **новый**
  `refresh_token`.
- **Ротация**: успешный refresh инвалидирует старый refresh-токен. Хранилище нужно
  обновлять **атомарно и целиком** (access + refresh + expiresAt вместе); при гонке
  двух одновременных refresh один проиграет.
- **Reuse-detection**: повторная отправка уже израсходованного refresh-токена
  трактуется как компрометация и может **отозвать всю семью** токенов этого гранта
  → разлогин. Именно поэтому MVP запретил виджету рефрешить **заимствованный** токен
  CC: семья там принадлежит Claude Code.

## 6. Разделение грантов — подтверждено (с оговоркой)

Собственный логин виджета — это **отдельный прогон authorization_code + PKCE** →
сервер выдаёт **самостоятельный грант** со **своей семьёй refresh-токенов**,
независимой от гранта Claude Code. Reuse-detection работает **на уровне семьи/гранта**,
поэтому ротация токена виджета не задевает токены CC и наоборот.

**Оговорка (важна для тикета модели кредов #17 и хранения #19):** независимость
держится, только если виджет **хранит свои токены отдельно** и **никогда не пишет
в `~/.claude/.credentials.json`**. Если виджет в режиме «own login» перезапишет файл
CC, он смешает две семьи в одном хранилище — и его рефреши начнут ротировать/ломать
грант Claude Code. Значит:

- **borrow-режим**: читать `~/.claude/.credentials.json` строго read-only, не рефрешить
  (как в MVP).
- **own-login-режим**: писать/рефрешить **только** собственный файл виджета
  (напр. `%APPDATA%/WidgetSubscription/credentials.json`), формой можно повторить
  `{ claudeAiOauth: { accessToken, refreshToken, expiresAt } }`, но **это своя копия**,
  не файл CC.

## 7. Что это даёт для доступа к usage-эндпоинту

Токен собственного гранта используется так же, как заимствованный (тикет #2):
`GET https://api.anthropic.com/api/oauth/usage` c
`Authorization: Bearer <access_token>`, `anthropic-beta: oauth-2025-04-20`,
`anthropic-version: 2023-06-01`. Для этого достаточно scope `user:inference`
(входит в запрошенный набор). Грант по **claude.ai** (подписка) — тот, что несёт
лимиты подписки; Console/API-грант для этой цели не подходит.

## 8. Следствия для последующих решений карты

- **#17 (модель кредов и выбор режима)**: borrow (read-only файл CC, без рефреша) vs
  own login (свой файл, свой рефреш). Развилка «два хранилища» из §6 — материал этого
  решения. Возможен и авто-выбор: есть валидный файл CC → borrow; нет → предложить own
  login.
- **#18 (как виджет логинится)**: loopback-listener (§3a) как основной UX + ручная
  вставка (§3b) как фолбэк; client_id/scope/PKCE/endpoints из §1–§4.
- **#19 (хранение и рефреш своего токена)**: атомарная перезапись собственного файла,
  refresh при `now + skew >= expiresAt`, single-flight против гонок, учёт ротации и
  reuse-detection (§5); хранить отдельно от файла CC (§6).
- **Туман «общий auth-порт в Core»**: два источника кредов с разным жизненным циклом
  (read-only borrow vs рефрешируемый own) — но оба уже прячутся за `ICredentialSource`
  рядом с адаптером (тикет #6). Отдельный порт в `Core` по-прежнему не обязателен;
  окончательно решается в #17.

## Источники

- Живая проверка usage-эндпоинта и форма `~/.claude/.credentials.json`:
  `docs/research/ticket-2-data-source.md` (тикет #2).
- Официальная документация Anthropic по OAuth для коннекторов (loopback/native-app,
  RFC 8252): <https://claude.com/docs/connectors/building/authentication>.
- Реверс-реализации флоу Claude Code (endpoints, PKCE, форма запросов/ответов,
  ротация refresh):
  - <https://gist.github.com/ben-vargas/c7c7cbfebbb47278f45feca9cef309d1>
  - <https://gist.github.com/cedws/3a24b2c7569bb610e24aa90dd217d9f2>
