# Motiva — Stage 2. Архитектура и API (ревизия после H1)

Статус: исправленная версия, ожидает утверждения. Реализация не пишется, бизнес-правила не меняются.
Входы: `docs/01_BUSINESS.md` v3.0 (B01–B37, E01–E12), `docs/02_ENGINEERING.md` v3.0 (T01–T10), `docs/03_AUTH.md` + `auth/jwt.py`, `.editorconfig`, утверждённый `docs/stage-1.md`. Исходная сдача этапа 2 сохранена в Git (коммит a847d7d).
Контракт API: `docs/openapi.yaml` (OpenAPI 3.0.3); этот документ согласован с ним.

---

## 1. Границы модулей и зависимости

### 1.1 Проекты и ответственность

| Проект | Ответственность | Запрещено |
|---|---|---|
| Motiva.Domain | Сущности и чистые правила: календарь периодов/сезонов (NodaTime), min-правило, аудитория по тегам, соревновательное ранжирование, целочисленная арифметика с проверкой переполнения | Зависимости кроме BCL/NodaTime: нет EF, HTTP, SDK, Valkey, S3 |
| Motiva.Application | Use cases по функциям, бизнес-авторизация (роль, владение, grants, активность, аудитория), порты, идемпотентность, границы транзакций через `IUnitOfWork` | Ссылки на Infrastructure/EF/Npgsql/S3; IQueryable наружу |
| Motiva.Infrastructure | Реализация портов: EF Core 10 + Npgsql (PostgreSQL 17), Valkey 9, S3, outbox, маппинги, миграции | Бизнес-решения; знания об endpoint'ах |
| Motiva.Api | ASP.NET Core: JWT, endpoint'ы, ProblemDetails, ETag/If-Match, Idempotency-Key, DTO | Прямые обращения к DbContext/Npgsql/S3; бизнес-правила |
| Motiva.Worker | Финализация челленджей, формирование выписок, очистка, диспетчер outbox | Те же запреты; выпуск токенов |

`Contracts`-проект не вводится: единственный контракт — `openapi.yaml` + DTO Api; дублирование не решает проблему (T02).

### 1.2 Направленные зависимости, сборка и архитектурные проверки

```
Motiva.Api  ─┐                       ┌─  Motiva.Worker
             ├─▶ Motiva.Application ─▶ Motiva.Domain
             │        ▲
Motiva.Infrastructure ┘ (реализует порты Application)
```
- Api/Worker → Application (всегда); → Infrastructure — только composition root (`Program.cs`, `AddMotivaApplication()`/`AddMotivaInfrastructure()`). Endpoint'ы и worker-orchestration не резолвят инфраструктурные типы.
- Infrastructure → Application (порты), Domain. Application → только Domain.
- Согласование зависимостей и проверок (L01): два взаимодополняющих механизма. (1) Roslyn BannedApiAnalyzers — запрет обращений к `DbContext`/Npgsql/S3-типам вне Infrastructure и файлов composition root, срабатывает на компиляции; (2) тесты направленных зависимостей (NetArchTest) — рёбра проектов и правила namespace (Domain без внешних пакетов, Application без Infrastructure), падают в отдельной группе CI; проверяется, что подсаженное нарушение действительно роняет проверку. NodaTime в Domain разрешена явно: чистая библиотека без транзитивных инфраструктурных зависимостей.
- Внутри — группировка по функциям: `Progress`, `Rewards`, `Wallets`, `Budgets`, `Campaigns`, `Competitions`, `Exports`, `Administration`, `Audit`.

### 1.3 Добавленные абстракции (порт → решаемая проблема)

| Порт в Application | Решаемая проблема | Реализация |
|---|---|---|
| `IUnitOfWork` | Атомарность multi-агрегатного use case (B17) без утечки EF | EF Core transaction |
| `IEmployeeDirectory` | Профили/теги/активность для авторизации и прогресса | EF |
| `ICampaignCatalog` | Настройки кампаний (включая набор ресурсов и аудитории заданий), проверка публикации | EF |
| `IWalletLedger` | Кошелёк, балансы, операции и движения с блокировками строк | EF + `FOR UPDATE` |
| `IBudgetLedger` | Бюджеты пар «кампания×ресурс» и выделения | EF + `FOR UPDATE` |
| `IProgressLog` | События, прогресс, завершения, очки стримов | EF |
| `ICompetitionBoard` | Счёт и неизменяемые итоги челленджей | EF + advisory lock |
| `IIntegrationDirectory` | Grants (с полными сочетаниями целей) и системы покупок | EF |
| `IExportStore` | Заказы выписок, снимок, ссылки | EF |
| `IAuditLog` | Журнал изменений прав/настроек/бюджета (B28.3) | EF |
| `IBackgroundJobs` | Атомарное планирование вместе с бизнес-commit | `outbox_jobs` |
| `ICacheSnapshots` | Каталог и live-рейтинг с `asOf` и допусками 30/5 с | Valkey |
| `IFileStorage` | Неизменяемые байты выписок, контрольные суммы | S3 |
| `TimeProvider` | Управляемое время (T05) | встроенная абстракция |

Репозитории per-entity и MediatR не вводятся (T02).

---

## 2. Модель данных

Соглашения: PostgreSQL 17; `company_id` на каждой строке (тенант из токена); идентификаторы UUID v7; время `timestamptz` UTC; суммы `bigint` (баланс/бюджет ≤ 10¹⁵, цели/дельты/очки ≤ 10⁹); коды хранятся нормализованным верхним регистром; внешние номера 1–100 ASCII, регистрозависимы. EF-сущности не являются DTO.

### 2.1 Таблицы

| Таблица | Существенные поля | Ключи / ограничения | Индексы |
|---|---|---|---|
| companies | id, name, time_zone | PK id | — |
| employees | company_id, master_id, is_active, created_at | PK (company_id, master_id) | (company_id, is_active) |
| employee_tags | company_id, master_id, tag | PK (company_id, master_id, tag); tag `[a-z0-9-]{1,32}` | — |
| resources | company_id, id, code_norm, name, status | unique (company_id, code_norm); код неизменяем | (company_id, status) |
| achievements | company_id, id, code_norm, name, description | unique (company_id, code_norm) | — |
| campaigns | company_id, id, code_norm, season, owner_master_id, starts_at, ends_at, status, published_at | unique (company_id, season, code_norm); ends > starts; сезон — use case + тест | (company_id, status, season) |
| campaign_tags | campaign_id, kind(any/all/none), tag | PK (campaign_id, kind, tag) | — |
| campaign_resources | campaign_id, resource_id | PK пара; состав управляется API до публикации (A2-01) | — |
| streams | campaign_id, id, code_norm, name, status | unique (campaign_id, code_norm) | — |
| tasks | stream_id, id, code_norm, name, description, goal, period, stream_points, status | unique (stream_id, code_norm); goal 1..10⁹; points 0..10⁹ | — |
| task_tags | task_id, kind(any/all/none), tag | PK (task_id, kind, tag) — аудитория задания (A2-02), проверяется совместно с аудиторией кампании | — |
| task_reward_items | task_id, resource_id, amount | PK (task_id, resource_id); amount 1..10⁹ | — |
| milestones | stream_id, id, threshold, achievement_id | unique (stream_id, threshold, achievement_id); threshold > 0 | — |
| challenges | campaign_id, stream_id, id, starts_at, ends_at, finalized_at | интервал внутри сроков — use case; кратность не ограничена (AMB-14) | (campaign_id), (stream_id) |
| budgets | campaign_id, resource_id, allocated_total, spent_total, returned_total | PK пара; CHECK `allocated_total − spent_total + returned_total ≥ 0` | — |
| operations | company_id, id, kind, result, refusal_code, initiator_type, initiator_master_id, initiator_subject, master_id, campaign_id, purchase_system_id, original_operation_id, reason, source_number, created_at | unique (company_id, initiator_key, kind, source_number); unique partial `(original_operation_id) WHERE kind IN (AwardReversal, SpendReversal) AND result = 'Posted'` (B25.4) | (company_id, master_id, created_at desc), (company_id, campaign_id, kind, created_at desc), (original_operation_id) |
| operation_items | operation_id, resource_id, amount | PK (operation_id, resource_id); amount > 0 | (resource_id) |
| wallets | company_id, master_id, id | unique (company_id, master_id) | — |
| wallet_balances | wallet_id, resource_id, balance | PK пара; CHECK `balance ≥ 0` | — |
| progress_events | company_id, source_subject, event_number, id, master_id, task_id, transmitted_delta, credited_delta, result, reject_reason, accepted_at | unique (company_id, source_subject, event_number); невалидные по данным не сохраняются (B16.5) | (task_id, accepted_at), (company_id, master_id, accepted_at desc) |
| progress_state | company_id, master_id, task_id, period_start, current | PK 4; current ≥ 0 | — |
| completions | company_id, master_id, task_id, period_start, id, stream_points, award_operation_id, reward_outcome, completed_at | unique (company_id, master_id, task_id, period_start) — B15.1 | (task_id, completed_at) |
| stream_points | company_id, master_id, stream_id, season, points | PK 4; points ≥ 0 — блокируемый агрегат очков (A2-06) | — |
| achievement_grants | company_id, master_id, achievement_id, season, granted_at | PK 4 — B30.2 | — |
| challenge_scores | challenge_id, master_id, score | PK пара; score ≥ 0 | — |
| challenge_results | challenge_id, master_id, score, place | PK пара; пишется при финализации, пути UPDATE нет (B33.3) | — |
| integration_grants | company_id, id, subject, kind, campaign_id, resource_id, purchase_system_id, status, granted_by, created_at, revoked_at | CHECK полноты цели: Progress → только campaign_id; Award → campaign_id + resource_id; Spend → resource_id + purchase_system_id, campaign_id NULL (A2-05) | (company_id, subject) |
| purchase_systems | company_id, id, code, name, status | unique (company_id, code) | — |
| purchase_system_resources | system_id, resource_id | PK пара | — |
| export_requests | company_id, id, requested_by, scope, target_master_id, resource_id, from_utc, to_utc, status, frozen_at, ready_at, size_bytes, checksum, error_detail, created_at, lease_until | статус Pending/Forming/Ready/Error/Deleted | (company_id, requested_by, created_at desc) |
| export_operation_ids | export_id, operation_id | PK пара — материализованный снимок B36 | (export_id) |
| download_links | id, export_id, expires_at, created_at, idem_key | expires ≤ created + 60 с | (export_id) |
| idempotency_keys | company_id, initiator_key, operation, target_id, key, response_status, response_body, created_at | PK составной; TTL ≥ 24 ч | — |
| outbox_jobs | id, type, payload, available_at, attempts, status, locked_by, locked_until | `FOR UPDATE SKIP LOCKED`; lease с продлением | (status, available_at) |
| audit_records | company_id, id, actor…, action, entity_type, entity_id, changes[{field, from, to}], created_at | append-only | (company_id, created_at desc), (entity_type, entity_id) |

Выделения бюджета и ручные начисления — строки единой книги `operations` (kind BudgetAllocation / ManualAward).

### 2.2 Бюджеты по ресурсам (отдельно)

- Единица учёта — строка `budgets` на пару «кампания × ресурс» (B08.6). Ресурс должен входить в `campaign_resources` (управляется владельцем/Admin через API до публикации — A2-01); строка бюджета создаётся первым выделением (`INSERT … ON CONFLICT` — «первая операция» с новым агрегатом, см. §3).
- `allocated_total` растёт только BudgetAllocation (инициатор Admin, B18.1); инвариант `allocated − spent + returned ≥ 0` держится тремя уровнями: `FOR UPDATE` строки бюджета, атомарное обновление счётчиков, CHECK (T06). Уменьшение/перенос бюджета отсутствуют (B18.5).
- Траты и возвраты трат бюджет не меняют (B23.6, B27.3); отмены начислений увеличивают `returned_total` только для проведённых начислений этой кампании (B19.1, B26.2).

### 2.3 Единый кошелёк и движения (отдельно)

- `wallets` 1:1 с профилем с создания; смены владельца/удаления нет ни на уровне API, ни путей записи (B01.5, B22.5).
- Баланс по ресурсу — одна строка `wallet_balances` независимо от кампании-источника (B22.2); CHECK `balance ≥ 0` + блокировка при трате/отмене начисления (B22.4).
- Движение = `operation_items` операции с `result = Posted`; знак задаёт вид операции: TaskReward/ManualAward/SpendReversal — кредит; Spend/AwardReversal — дебет. Отклонённые операции хранят попытленные позиции, но в балансы и CSV не входят (B28.2, T07).
- Книга едина (B24/B25/B28): номер источника, инициатор, результат; коррекция — только полной отменой со ссылкой на оригинал; одна успешная отмена на оригинал (частичный уникальный индекс).

### 2.4 Удаление и история

| Данные | Правило |
|---|---|
| Опубликованные кампании/стримы/задания, ресурсы, достижения, сотрудники | Физического удаления нет: архив/блокировка; черновик кампании удаляется целиком с дочерними (B09.8) |
| operations, operation_items, progress_events, completions, stream_points, achievement_grants, challenge_results, audit_records | Append-only / только монотонные счётчики; коррекция — операцией отмены (B25.1) |
| export_requests | Мягкое Deleted; объект S3 и незавершённые загрузки чистятся ≤ 10 мин после восстановления зависимостей (T07) |
| download_links, idempotency_keys | Истечение срока / TTL ≥ 24 ч, фоновая очистка |

---

## 3. Инварианты: где обеспечиваются → сценарий → проверка

| Инвариант | Где обеспечивается | Конкурентный/сбойный сценарий | Как проверить |
|---|---|---|---|
| Пакетная награда целиком или ничего: B12.3, B17.1, B20.1–B20.4 | Use case завершения: одна транзакция — прогресс, завершение, очки, достижения, решение; строки `budgets` пакета блокируются в порядке id ресурса; одна операция TaskReward Posted со всеми позициями либо Declined | Награда 8A+5B при B=1: ни одного движения, завершение/очки/рейтинг сохранены; повтор события после пополнения — прежний результат (SCN-02) | Functional: балансы/бюджеты/операции после отказа; unit: матрица решения; мутация «убрать item» роняет тест |
| Последнее доступное средство: B19.2–B19.4, B20.6 | `FOR UPDATE` бюджета + CHECK; транзакция отклоняется целиком | Бюджет 10, две параллельные награды по 10 → одна Posted, одна Declined, остаток 0 (E04) | Параллельный functional (2 потока, PG); сценарий «два конкурента» в нагрузке |
| Повтор и конфликт номера: B16.2–B16.3, B24.2–B24.3, T05 | Единый порядок (A2-07): (1) аутентификация/тенант; (2) текущая авторизация — роль, владение, действующий grant, активность профиля (включая повтор) → 401/403 без любых эффектов, ошибка доступа не создаёт ресурс и не превращается в сохранённый успех; (3) поиск номера: новый → обработка (результат может быть Declined/Rejected); тот же номер + те же существенные данные → исходный статус (201) и исходное представление, ничего не меняется; тот же номер + иные данные → 409 `conflict.business-number`, ничего не создаётся. Существенные данные: событие — masterId, taskId, переданная дельта; трата — получатель, система, ресурс, сумма; ручное начисление — получатель, ресурс, сумма; отмена — оригинал; выделение — ресурс, сумма. Перестановка позиций награды несущественна. Уникальность в PG, не в кеше | Ответ потерян, рестарт процесса → повтор возвращает исходный operationId/результат без второго эффекта; тот же номер с другой суммой → 409; grant отозван до повтора → 403, прежний результат не аннулирован и новый не создан; сотрудник заблокирован до повтора → 403 `authz.employee-not-active`; конкурентный повтор — короткое ожидание блокировки, при таймауте 503/Retry-After | E2E: kill API между commit и ответом; HTTP-тесты повтора/409/403; parallel-тест конкурентного повтора |
| Отмена: B25.2–B25.4, B26.2–B26.3, B27.1–B27.5 | Unique partial index «одна Posted-отмена на оригинал»; отмена награды блокирует все `wallet_balances` пакета (в порядке id ресурса), нехватка одного баланса → Declined целиком (E08); возврат трат бюджет не трогает | Два одновременных возврата → один Posted (E07); отмена при частично потраченной награде → Declined | Параллельный functional; persistence-тест индекса |
| Смена прав: B05.1–B05.2, B16.4 | Права читаются в транзакции операции из PG (не из кеша, T06); блокировка запрещает новые начисления/траты и действия от имени сотрудника, включая повторы; административная отмена не блокируется | Блокировка между отправкой и обработкой: новая трата Declined, новое событие Rejected, повтор — 403; прежние результаты сохранены | Functional с блокировкой посреди потока; проверки из перечня 03_AUTH |
| Параллельные завершения и связанные агрегаты: B17.1, B29.3, B30.2 (A2-06) | Протокол связанных изменений: (а) строка `stream_points` (сотрудник+стрим+сезон) блокируется `FOR UPDATE` в транзакции завершения, очки задания добавляются к монотонному агрегату, пересечённые пороги вех обрабатываются в этой же транзакции, вставки `achievement_grants` идут с `ON CONFLICT DO NOTHING` по PK (уже выдано за сезон — пропуск, B30.2); (б) первые операции с новыми агрегатами (`progress_state`, `wallet_balances`, `budgets`, `stream_points`, `challenge_scores`) — `INSERT … ON CONFLICT`/повтор после unique violation с перечитыванием `FOR UPDATE` и продолжением: конфликт первого вставления — не бизнес-ошибка; (в) конфликт уникальности `completions` — уже существующее завершение: идемпотентный возврат сохранённого результата без вторых очков | Два разных задания одного стрима завершаются одновременно и в сумме пересекают порог вехи → достижение выдано ровно один раз; два сотрудника одновременно получают первые балансы одного ресурса → обе строки созданы, ни один конфликт не потерян | Параллельный functional: два завершения + порог между ними → одна выдача; конфликт первого завершения → один набор очков; мутация «снять блокировку агрегата» роняет тест на дубль/потерю |
| Границы соревнования: B13.4, B31.2, B33.1–B33.5 | `accepted_at` = момент принятия в транзакции; приращение `challenge_scores` только при `accepted_at < ends_at`; финализация и транзакция принятия берут `pg_advisory_xact_lock(challenge)` (короткий захват) → поздние commit не теряются и не попадают в новый интервал | Событие за 1 мс до конца — в счёте; после конца — нет; повтор на следующий день не переносится (B33.5); финал ≤ 5 с (T07) | Unit-календарь [start,end); functional на границе с TimeProvider; e2e тайминг |
| Снимок, повторное формирование, финализация и удаление выписки: B36, B37, T06, T07 (A2-08) | Протокол с двумя worker'ами: (1) заморозка — условный `UPDATE … SET status='Forming', frozen_at=now() WHERE id=? AND status='Pending'`; в той же транзакции материализуется `export_operation_ids`; переход `Error→Forming` снимок НЕ пересчитывает (единственная заморозка, поздние commit не расширяют); (2) формирование — потоковая выгрузка CSV из снимка, checksum SHA-256 при записи, S3-ключ детерминирован; (3) Ready — условный `UPDATE … WHERE status='Forming'` после проверки контрольной суммы; ссылка выдаётся только на Ready с совпадающим checksum; (4) после сбоя job повторяется (outbox, backoff, lease `locked_until` с продлением и перекладыванием по `SKIP LOCKED`) — шаги идемпотентны: повторная загрузка тех же байтов поверх того же ключа даёт тот же checksum, crash между загрузкой и Ready восстанавливается; (5) удаление — `UPDATE … SET status='Deleted'` условно из любого не-Deleted статуса: немедленно запрещает новые ссылки, условный переход worker'а в Ready проваливается — отчёт не воскрешается; (6) очистка — отдельная задача удаляет объект S3 и незавершённые multipart-загрузки удалённых/истёкших выписок ≤ 10 мин после восстановления зависимостей, строки ссылок удаляются, история запроса (`export_requests`, Deleted) остаётся | Движение между заморозкой и формированием не входит в повтор (SCN-07); два worker'а берут один экспорт — lease не допускает дубля; crash между загрузкой и Ready → повтор даёт те же байты и Ready; удаление в момент формирования → статус Deleted, worker не переводит в Ready, объект будет очищен | E2E chaos (S3 off 30 с, повтор задачи); функциональный тест фиксированного снимка; тест гонки удаления; тест двух worker'ов на одном экспорте |
| Мин-кап и одно завершение: B14.4, B15.1, B15.3 | Блокировка `progress_state`; min(goal, current+delta); unique завершения; события после цели — credited 0 | Цель 3: 2+5 → 3/зачтено 1; новое +1 → зачтено 0 (E03); укороченный первый период (EDGE-01) | Unit + functional |
| Достижение раз в сезон: B29.3, B30.2 | PK `achievement_grants` | Две вехи одного достижения → одна выдача (EDGE-02) | Functional |
| Кошелёк не отрицателен: B22.4, E05 | CHECK + блокировка баланса | Две траты по 7 при балансе 10 → одна Posted, остаток 3 | Параллельный functional + нагрузка |
| Периоды/сезоны/таймзона: B10, B13 | Календарь Domain (NodaTime), TimeProvider | Границы периодов, смена сезона, DST 25.10.2026 | Unit на зафиксированном времени |
| Изоляция компаний: B02, E12 | `company_id` из токена во всех запросах; чужое — 404 | masterId=123 в двух компаниях | Contract-тесты двух компаний |

---

## 4. API

Полный контракт: `docs/openapi.yaml` — OpenAPI **3.0.3**, базовый путь `/api/v1`, единственная версия; nullable-поля оформляются единым стилем `nullable: true` (самостоятельные type-массивы 3.1 не используются) — L02.

### 4.1 Соглашения

| Аспект | Решение |
|---|---|
| Аутентификация | `Authorization: Bearer` JWT по 03_AUTH (HS256; iss/aud; `companyId/actorType/masterId/role`); дубликаты identity-claims отклоняются |
| Статусы | 201+Location (создание; экономический отказ — тоже 201 с `result: Declined`); 200 чтение; 204 удаление; ошибки — Problem Details (RFC 9457) с постоянным `code` и `traceId` |
| Повторы (A2-07) | Порядок обработки: текущая авторизация первой (отказ 401/403 — без эффектов и без создания ресурса) → бизнес-номер/Idempotency-Key: тот же номер/ключ + те же существенные данные → исходный статус и представление; иные данные → 409; конкурентный повтор — ограниченное ожидание или 503/Retry-After с обязанностью последующего повтора восстановить результат. Бизнес-номера бессрочны в PG; остальные создающие POST — `Idempotency-Key` ≥ 24 ч |
| Конкурентные правки настроек | Strong ETag на GET каждого изменяемого объекта (включая grants, milestones, challenges — GET по id); `If-Match` на **каждой** операции изменения настроек (PATCH/PUT/DELETE, включая отзыв grant): нет — 428, устарел — 412. Экономические операции и удаление выписки If-Match не используют (T04) |
| Списки | Все коллекции без исключения: `items + nextCursor`, limit 1..100 (по умолчанию 50), устойчивый порядок (`createdAt, id`; каталоги — по коду; рейтинг — счёт↓, masterId↑); keyset-курсор не пропускает и не дублирует существовавшие записи (A2-11) |
| Кеш-свежесть в ответе (A2-09) | Каждый кешируемый неперсональный список (ресурсы, достижения, кампании, стримы/задания, live-рейтинг) несёт обязательный `asOfUtc` — момент снимка данных; правило возраста: TTL отсчитывается от `asOfUtc` (не от момента записи в Valkey); значение с `expiresAtUtc ≤ now` — miss, «мёртворождённые» записи не пишутся; гарантия для обслуженного ответа: `now − asOfUtc ≤ 30 с` (каталог) / `≤ 5 с` (live-рейтинг); чтение из PG даёт `asOfUtc` = момент чтения; Final-рейтинг неизменяем — `asOfUtc` = момент финализации, допуск не применяется |
| Живой рейтинг | Top N через `limit`; собственное место — отдельный `GET …/leaderboard/me` |
| Время/числа | Входные даты с UTC offset; выход UTC; целые с проверкой переполнения (превышение — 400 без частичных эффектов) |

### 4.2 Коды ошибок (Problem Details `code`)

| HTTP | code | Случай |
|---|---|---|
| 400 | `validation.failed` / `validation.overflow` / `validation.code-format` / `validation.grant-target` | Поля, форматы, пределы чисел, неполное/недопустимое сочетание цели grant (A2-05) |
| 401 | `auth.invalid-token` | Подпись, iss/aud, срок, обязательные claims |
| 403 | `authz.forbidden` / `authz.grant-missing` / `authz.employee-not-active` | Роль/владение/grant/аудитория; действие от имени заблокированного профиля (включая повторы) |
| 404 | `not-found` | Чужой или несуществующий объект компании |
| 409 | `conflict.business-number` / `conflict.idempotency-data` / `conflict.state` / `conflict.publish-check` | Номер с иными данными; неизменяемое состояние; провал проверки публикации |
| 412 / 428 | `precondition.failed` / `precondition.required` | If-Match |
| 424 | `dependency.unavailable` | Выдача ссылки при временном отказе S3 |
| 503 | `service.unavailable` | PostgreSQL недоступен (ограниченный по времени), Retry-After |

Экономические отказы (201 + `result: Declined`): `InsufficientBudget`, `InsufficientFunds`, `InsufficientBalance`, `ResourceUnavailable`, `RecipientNotActive`, `RecipientNotInAudience`, `OriginalAlreadyReversed`, `OriginalNotPosted`. Отказы событий — `result: Rejected` с `rejectReason` (EmployeeNotActive, AudienceMismatch, CampaignClosed, TargetArchived); по умолчанию занимают номер (AMB-08); повтор возвращает сохранённый отказ, если текущая авторизация прошла.

### 4.3 Карта ресурсов (метод → права)

| Ресурс | Методы | Права |
|---|---|---|
| /employees, /employees/{masterId} | GET, POST, PATCH | Admin (GET своего профиля — сотрудник) |
| /resources, /resources/{id} | GET, POST, PATCH | GET — все участники компании; запись — Admin |
| /achievements, /achievements/{id} | GET, POST, PATCH | GET — все; запись — Admin |
| /campaigns, /campaigns/{id} | GET, POST, PATCH, DELETE | GET — сотрудник (опубликованные текущего сезона), владелец, Admin; создание — Admin; настройка (name/description/audience/status) — владелец своей или Admin; DELETE — только черновик |
| /campaigns/{id}/owner | PUT | **Только Admin** — назначение/смена владельца отдельным полномочием (A2-04); действует для новых обращений сразу |
| /campaigns/{id}/resources | GET, PUT | Владелец/Admin; PUT (состав набора) — только черновик, иначе 409; каждый ресурс — активный ресурс компании (A2-01) |
| /campaigns/{id}/streams, /campaigns/{id}/challenges, /streams/{id}/tasks, /streams/{id}/milestones | GET, POST | GET — аудитория опубликованной кампании, владелец, Admin; создание — владелец/Admin (челлендж — только до публикации) |
| /streams/{id}, /tasks/{id} | GET, PATCH | GET — аудитория/владелец/Admin; PATCH — владелец/Admin (после публикации — только архивация) |
| /milestones/{id}, /challenges/{id} | GET, DELETE | GET — аудитор/владелец/Admin; DELETE — только черновик (владелец/Admin) |
| /campaigns/{id}/budgets, /campaigns/{id}/budget-allocations | GET, POST | GET — владелец/Admin; выделение — Admin; ресурс обязан входить в набор кампании |
| /campaigns/{id}/progress | GET | Владелец (прогресс участников своей кампании), Admin (A2-03) |
| /integration-grants, /integration-grants/{id} | GET, POST, DELETE | Admin; цели задаются строгими сочетаниями (A2-05): Progress → campaign; Award → campaign + resource; Spend → resource + purchase system |
| /purchase-systems, /purchase-systems/{id} | GET, POST, PATCH | Admin |
| /progress-events | POST, GET | POST — интеграция с действующим grant Progress на кампанию задания; GET — Admin |
| /spends | POST | Сотрудник (свой кошелёк) или система покупок с grant Spend (ресурс + система) |
| /campaigns/{id}/manual-awards | POST | Владелец/Admin; интеграция — grant Award (кампания + ресурс) |
| /reversals | POST | Admin; текущий владелец кампании-источника (начисления); система покупок — свои траты при действующем grant Spend (та же пара ресурс+система) |
| /me/wallet, /me/achievements, /me/campaigns/{id}/progress | GET | Сотрудник — свои |
| /me/operations | GET | Сотрудник — операции своего кошелька; service-субъект — операции, инициатором которых он является (своя история, не весь кошелёк) (A2-03, B04.3) |
| /employees/{masterId}/wallet, /employees/{masterId}/operations | GET | Admin |
| /operations/{id} | GET | Инициатор, задействованный сотрудник, Admin, своя система покупок |
| /campaigns/{id}/operations | GET | Владелец (расходы кампании), Admin |
| /challenges/{id}/leaderboard, …/leaderboard/me | GET | Текущая аудитория; после финала — участники (наличие своего результата), владелец, Admin (AMB-06 — окно до конца сезона: по умолчанию аудитория) |
| /audit-records | GET | Admin |
| /exports, /exports/{id}, /exports/{id}/download-links | POST, GET, DELETE | Сотрудник — свои; Admin — сотрудник/компании своей фирмы; DELETE — заказчик |

Создания без бизнес-номера требуют `Idempotency-Key`; progress-events, spends, manual-awards, reversals, budget-allocations защищены собственными номерами источников.

---

## 5. Короткие решения

**Аутентификация/авторизация.** JwtBearer HS256, ключ — декодированные Base64-байты, явный маппинг claims. Роль Admin — из токена (T03); владение, grants, активность, аудитория — данные приложения, проверяются в транзакции операции (не из кеша). Назначение владельца — отдельное Admin-полномочие (PUT /campaigns/{id}/owner); владелец настраивает свою кампанию без права переназначить себя (A2-04). Service-токен не имеет кошелька; masterId из запроса не подменяет субъект. Bootstrap — одна доверенная команда создания компании и активного администратора через use case.

**Кеш.** Valkey: каталог (30 с) и live-рейтинг (5 с), чистый TTL без инвалидации; запись несёт `{payload, asOfUtc, expiresAtUtc = asOfUtc + допуск}`; возраст считается от `asOfUtc`, позднее заполнение не продлевает жизнь, значение с истёкшим `expiresAtUtc` — miss и не пишется (A2-09); `asOfUtc` обязателен в кешируемых ответах; при отказе — ограниченное ожидание (~200 мс) и чтение из PG. Права, балансы, экономические решения, лимиты выполнения из кеша не берутся (T06).

**Фоновые операции.** Outbox в PostgreSQL (`FOR UPDATE SKIP LOCKED`, lease с продлением, два worker'а): FinalizeChallenge (доступна с `ends_at`, поллинг 1 с — SLO 5 с), ExportFormation (протокол §3: единственная заморозка, идемпотентные шаги, условные переходы, восстановление после crash), Cleanup (ссылки, объекты и незавершённые загрузки удалённых выписок ≤ 10 мин, idempotency ≥ 24 ч). Задача ставится в транзакции бизнес-изменения.

**Отказы.** PostgreSQL недоступен → ограниченный 503 + Retry-After, readiness «не готов», liveness не падает; Valkey → деградация до PG; S3 → основной учёт продолжается, экспорты повторяются; логи/метрики OpenTelemetry, correlation/traceId в ProblemDetails; токены, ключи, signed URL не логируются.

**Нагрузка.** 85% чтений (каталог/кошелёк/рейтинг) — Valkey + точечные индексированные чтения; 15% записей — короткие транзакции на горячих строках; пулы Npgsql ограничены под 1 GiB; UUID v7 для локальности; целевые p95 ≤ 200/500 мс; проверяются итоговые бизнес-состояния. Условия эксперимента, не SLA.

**Слои тестов** (T08): Unit — правила Domain; Functional — реальные PG/Valkey/S3 через use cases и HTTP, включая параллельные сценарии §3; HTTP/contract — реальные JWT, статусы, ETag/If-Match, ProblemDetails, пагинация, повторы; Persistence/migrations — маппинги, индексы, CHECK, пустая БД и пошаговость; Architecture — анализаторы + направленные зависимости (L01); E2E — процессы, конкуренция, рестарты, отказы Valkey/S3/API/PG, неизменяемость снимка. Ожидания — из B-правил (SCN/EDGE).

### 5.1 Ключевые спорные решения (вариант → причина → цена)

| Решение | Причина | Цена/ограничение |
|---|---|---|
| Пессимистичные блокировки бюджета/баланса вместо serializable | Детерминированность на горячих строках, PG-only | Очередь конкурентов в один бюджет; для 15 RPS записей приемлемо |
| Единая книга operations | B28.2, один CSV-путь | Широкая схема с явными nullable-полями |
| Outbox в PostgreSQL, не брокер | T01 не даёт брокера; атомарность с commit | Поллинг 1 с; SLO 5 с достигается |
| Материализованный снимок выписки | T07 запрещает замену фильтром по timestamp | До ~10⁵ строк на выписку, чистятся с ней |
| NodaTime в Domain | Корректная арифметика периодов/DST | Одна чистая зависимость |
| Блокируемый агрегат stream_points (A2-06) | Сериализация одновременных завершений одного стрима без потерянных/дублей достижений | Короткая очередь на (сотрудник, стрим, сезон) |
| Declined как 201-ресурс | T04 | Клиент обязан читать `result` |
| Advisory-lock финализации челленджа | Гонка «принятие в момент конца» | Короткий лок на событие в активном челлендже |
| Возраст кеша от asOfUtc, «мёртворождённые» записи не пишутся (A2-09) | Допуск отставания выполняется безусловно | Редкая лишняя перестройка при задержанном заполнении |
| Кеш по TTL без инвалидации | Допуски покрывают устаревание | Окно не меньше допуска по конструкции |

---

## 6. Покрытие

### 6.1 B01–B37 → механизм

| B | Механизм |
|---|---|
| B01 | /employees POST: профиль+кошелёк одной транзакцией; уникальность masterId |
| B02 | company_id из токена во всех store/запросах; чужое — 404 |
| B03 | Матрица 4.3: чтение сотрудником (/campaigns GET, /campaigns/{id}/tasks, /me/*), владельцем (/campaigns/{id}/progress, budgets, operations), назначение владельца — PUT /owner только Admin |
| B04 | /integration-grants со строгими сочетаниями (oneOf + CHECK); /me/operations для service — своя история, не весь кошелёк |
| B05 | Проверки в транзакции операции; повторы тоже (§3 «Смена прав») |
| B06 | /resources: нормализованный код, уникальность, неизменяемость, нулевые балансы |
| B07 | Архивация PATCH статуса; Declined `ResourceUnavailable`; отмена после архивирования |
| B08 | Уникальные индексы кодов; **campaign_resources управляется GET/PUT /campaigns/{id}/resources (владелец/Admin, до публикации)**; budgets на пару |
| B09 | PATCH status → Published (проверка состава, 409 publish-check), неизменяемые поля (409), DELETE черновика |
| B10 | Календарь Domain; кампания в сезоне |
| B11 | Теговые наборы кампании И задания (task_tags + campaign_tags), проверка совместно + сроки; /me/campaigns/{id}/progress |
| B12 | Валидация заданий и reward items (ресурс из набора кампании) |
| B13 | Календарь периодов (NodaTime), TimeProvider |
| B14 | Use case принятия: min(goal, cur+delta), передано/зачтено, момент принятия |
| B15 | unique завершений; события после цели — credited 0 |
| B16 | unique (company, source, event_number); порядок auth→номер→результат (§4.1); 409 на расхождение |
| B17 | Одна транзакция use case; Declined/Rejected — бизнес-исходы, не 5xx |
| B18 | /budget-allocations (Admin, ресурс из набора кампании), история = операции |
| B19 | Счётчики budgets + CHECK; атомарность бюджет↔баланс |
| B20 | Решение в транзакции завершения; финальность |
| B21 | /manual-awards (владелец/Admin/grant Award); не меняет прогресс |
| B22 | /me/wallet; один баланс на ресурс; CHECK ≥ 0 |
| B23 | /spends; активность владельца кошелька; отказы 201 Declined |
| B24 | Номера операций, уникальность, порядок повтора, 409 |
| B25 | Append-only книга; отмены как операции; частичный индекс |
| B26 | /reversals для начислений (балансы пакета или Declined) |
| B27 | /reversals для трат (Admin/система покупок при действующем grant Spend) |
| B28 | Схема Operation; /audit-records |
| B29 | stream_points агрегат + вехи → достижения в транзакции завершения |
| B30 | PK achievement_grants; протокол параллельных завершений §3 |
| B31 | Челлендж до публикации; счёт из зачтённых дельт |
| B32 | Соревновательное ранжирование 100,100,90 → 1,1,3 (не dense), равные — masterId↑, top N + me |
| B33 | FinalizeChallenge job, immutable challenge_results |
| B34 | /me/*, /campaigns/{id}/progress (владелец), /campaigns/{id}/operations, Admin-представления, пагинация/фильтры |
| B35 | /exports (scope, ресурс/все, [from,to)) |
| B36 | Статусы; единственная заморозка снимка; повтор формирования на том же снимке |
| B37 | DELETE выписки (немедленный запрет ссылок, без воскрешения), ссылки ≤ 60 с, очистка ≤ 10 мин |

### 6.2 T01–T10 → как выполнено

| T | Как |
|---|---|
| T01 | Стек зафиксирован; версии SDK/образов — перед этапом 3; миграции только PG 17 |
| T02 | Проекты/зависимости §1 + анализаторы и арх-тесты (L01) |
| T03 | JWT §5, bootstrap-команда, без login endpoints, активность профиля в транзакции |
| T04 | REST §4; If-Match на всех изменениях настроек + ETag-источники (GET по id для grants/milestones/challenges); единый envelope всех списков; таблицы покрытия 6.1 |
| T05 | Порядок «авторизация → сохранённый результат → предусловия» §4.1; TimeProvider |
| T06 | PG-инварианты §3; правило возраста кеша §4.1/§5; 2 API + 2 worker |
| T07 | Протокол экспорта §3 (заморозка, идемпотентные шаги, Ready, ссылки, очистка) |
| T08 | Слои §5, включая параллельные тесты §3 |
| T09 | Подход §5; «два конкурента» §3 |
| T10 | Compose/README/примеры — этап 3 |

---

## 7. Противоречия, сопоставления и согласования L01–L03

Прямых противоречий входов не выявлено. Сопоставления: С1 — CSV «номер операции» = `operationId` (внешний номер — в API, header CSV фиксирован T07); С2 — «первые N» = `limit` списков (AMB-05); С3 — T07 закрывает AMB-07 (автоматическая финализация в момент конца); С4 — T07 закрывает AMB-13 (ссылка ≤ 60 с).

Локальные согласования по L01–L03 (первопричины исправлены в основных разделах):

| ID | Было | Стало |
|---|---|---|
| L01 | Зависимости и архитектурные проверки описаны расплывчато («падает на сборке») | Два согласованных механизма: BannedApiAnalyzers (компиляция — запрет инфраструктурных API вне Infrastructure/composition root) + NetArchTest (рёбра проектов и namespace, падающий тест на подсаженном нарушении); NodaTime в Domain разрешена явно (§1.2) |
| L02 | Текст заявлял OpenAPI 3.1 при файле 3.0.3; nullable описаны неединообразно | Зафиксировано 3.0.3 + единый стиль `nullable: true` (§4) |
| L03 | Неточный термин «dense rank» (даёт 1,1,2) и опечатка «Live/Float» | «Стандартное соревновательное ранжирование» 100,100,90 → 1,1,3; равные строки — по возрастанию masterId; бизнес-результат не менялся (§6.1 B32, OpenAPI) |

## 8. Открытые вопросы (нет решения заказчика)

Умолчания этапа 2 сохранены и не решаются молча; смена решения не ломает контракт.

| ID | Умолчание | Статус |
|---|---|---|
| AMB-02 | Владельцу разрешена отмена начисления заблокированному | ждёт заказчика |
| AMB-04 | «Участник» = активный сотрудник текущей аудитории кампании | ждёт заказчика |
| AMB-05 | N = limit списка (50/100) | ждёт заказчика |
| AMB-06 | В окне «конец кампании — конец сезона» рейтинг читает текущая аудитория | ждёт заказчика |
| AMB-08 | Отказы событий (кроме неверных данных) занимают номер | ждёт заказчика |
| AMB-09 | Повтор принятого события при потере аудитории возвращает сохранённый результат | ждёт заказчика |
| AMB-11 | «Успешное начало формирования» = транзакция перехода в Forming, она же фиксирует снимок | ждёт заказчика |
| AMB-12 | Состав проверки публикации: поля заданий, награды из набора кампании, вехи/достижения, челлендж в сроках, аудитории, сезон | ждёт заказчика |
| AMB-14 | Несколько челленджей у кампании разрешены | ждёт заказчика |
| Техн. | Выбор S3-образа и фиксация версий SDK/пакетов — перед этапом 3 (T01) | этап 3 |

Стадия 2 (после H1) завершена; к планированию/реализации не перехожу до утверждения.
