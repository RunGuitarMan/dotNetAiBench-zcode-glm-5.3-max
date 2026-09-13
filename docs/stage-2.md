# Motiva — Stage 2. Архитектура и API

Статус: первая сдача этапа 2, ожидает проверки. Реализация не пишется, бизнес-правила не меняются.
Входы: `docs/01_BUSINESS.md` v3.0 (B01–B37, E01–E12), `docs/02_ENGINEERING.md` v3.0 (T01–T10), `docs/03_AUTH.md` + `auth/jwt.py` (договор JWT), `.editorconfig`, утверждённый `docs/stage-1.md` (реестр правил, сценарии SCN/EDGE, открытые вопросы AMB).
Контракт API: `docs/openapi.yaml` (этот документ согласуется с ним; при расхождении приоритет у OpenAPI как единственного машинного артефакта, поправка регистрируется).

---

## 1. Границы модулей и зависимости

### 1.1 Проекты и ответственность

| Проект | Ответственность | Запрещено |
|---|---|---|
| Motiva.Domain | Сущности и правила без побочных эффектов: календарь периодов/сезонов (NodaTime), min-правило прогресса, аудитория по тегам, расстановка мест 1-1-3, целочисленная арифметика с проверкой переполнения | Любые зависимости кроме BCL/NodaTime: нет EF, HTTP, SDK, Valkey, S3 |
| Motiva.Application | Use cases по функциям (команды/запросы), бизнес-авторизация (владелец/grant/аудитория/активность), порты хранилищ, orchestrация идемпотентности, границы транзакций через `IUnitOfWork` | Ссылки на Infrastructure/EF/Npgsql/S3; IQueryable наружу |
| Motiva.Infrastructure | Реализация портов: EF Core 10 + Npgsql (PostgreSQL 17), Valkey 9, S3-клиент, outbox/поллинг, маппинг DTO↔сущности, миграции | Бизнес-решения; знания о HTTP-endpoint'ах |
| Motiva.Api | ASP.NET Core: JWT-аутентификация, endpoint'ы, ProblemDetails, ETag/If-Match, Idempotency-Key, DTO | Прямые обращения к DbContext/Npgsql/S3; бизнес-правила |
| Motiva.Worker | Background-сервисы: финализация челленджей, формирование выписок, очистка, диспетчер outbox | Те же запреты; выпуск токенов |

`Contracts`-проект не вводится: единственный контракт — `docs/openapi.yaml` и DTO проекта Api; дублирование контракта в отдельной сборке не решает никакую проблему (T02).

### 1.2 Направленные зависимости и сборка

```
Motiva.Api  ─┐                       ┌─  Motiva.Worker
             ├─▶ Motiva.Application ─▶ Motiva.Domain
             │        ▲
Motiva.Infrastructure ┘ (реализует порты Application)
```
- Api/Worker → Application (всегда) и → Infrastructure (только composition root в `Program.cs` через `AddMotivaApplication()`/`AddMotivaInfrastructure()`).
- Infrastructure → Application (порты), Domain (маппинг сущностей). Application → только Domain.
- Сборка приложения — ровно два composition root: `Motiva.Api/Program.cs` и `Motiva.Worker/Program.cs`. Endpoint/worker не резолвят инфраструктурные типы напрямую.
- Ограничения закрепляются архитектурными тестами (T02): Domain без внешних пакетов; Application не ссылается на Infrastructure; обращение к `DbContext`/`Npgsql`/S3-клиенту вне Infrastructure и composition root падает на сборке (проверяется подсадкой заведомого нарушения).

Внутри Application/Api — группировка по функциям: `Progress`, `Rewards`, `Wallets`, `Budgets`, `Campaigns`, `Competitions`, `Exports`, `Administration`, `Audit`.

### 1.3 Добавленные абстракции (порт → решаемая проблема)

| Порт в Application | Решаемая проблема | Реализация в Infrastructure |
|---|---|---|
| `IUnitOfWork` | Атомарность multi-агрегатного use case (B17) без утечки EF в Application | EF Core `IDbContextTransaction` |
| `IEmployeeDirectory` | Профили/теги/активность для авторизации и прогресса | EF |
| `ICampaignCatalog` | Настройки кампаний/стримов/заданий/вех/челленджей и проверки публикации | EF |
| `IWalletLedger` | Кошелёк, балансы, операции и движения с блокировками строк | EF + `SELECT … FOR UPDATE` |
| `IBudgetLedger` | Бюджеты пар «кампания×ресурс» и выделения | EF + `FOR UPDATE` |
| `IProgressLog` | События, состояние прогресса, завершения | EF |
| `ICompetitionBoard` | Счёт и неизменяемые итоги челленджей | EF + advisory lock |
| `IIntegrationDirectory` | Grants интеграций и системы покупок | EF |
| `IExportStore` | Заказы выписок, материализованный снимок, ссылки | EF |
| `IAuditLog` | Журнал изменений прав/настроек/бюджета (B28.3) | EF |
| `IBackgroundJobs` | Атомарное планирование задач вместе с бизнес-commit (не теряются при сбое) | Таблица `outbox_jobs` |
| `ICacheSnapshots` | Неперсональный каталог и live-рейтинг с `asOf` и допусками 30/5 с | Valkey |
| `IFileStorage` | Неизменяемые байты выписок, проверка контрольной суммы | S3 |
| `TimeProvider` | Управляемое время в тестах (T05) | Встроенная абстракция .NET |

Порты объединены по возможности использования в одном use case, а не по таблице на сущность; репозитории per-entity и MediatR не вводятся (T02).

---

## 2. Модель данных

Соглашения: PostgreSQL 17, все таблицы с `company_id` (мультитенантность B02, значения из токена, не из запроса); идентификаторы UUID v7 (упорядоченность для курсоров и индексов); время `timestamptz` в UTC; суммы `bigint`, баланс/бюджет ≤ 10¹⁵, цели/дельты/очки ≤ 10⁹; коды хранятся нормализованными в верхнем регистре (регистронезависимая уникальность B06.3/B08.2); внешние номера — 1–100 ASCII, регистрозависимы. EF-сущности не являются DTO.

### 2.1 Таблицы

| Таблица | Поля (существенные) | Ключи / ограничения | Индексы |
|---|---|---|---|
| companies | id, name, time_zone | PK id | — |
| employees | company_id, master_id, is_active, created_at | PK (company_id, master_id); master_id > 0 | (company_id, is_active) |
| employee_tags | company_id, master_id, tag | PK (company_id, master_id, tag); tag `[a-z0-9-]{1,32}` | — |
| resources | company_id, id, code_norm, name, status, archived_at | unique (company_id, code_norm); код неизменяем | (company_id, status) |
| achievements | company_id, id, code_norm, name, description | unique (company_id, code_norm) | — |
| campaigns | company_id, id, code_norm, season, owner_master_id, starts_at, ends_at, status, published_at | unique (company_id, season, code_norm); ends > starts; принадлежность сезону — проверка use case + тест (таймзона) | (company_id, status, season) |
| campaign_tags | campaign_id, kind(any/all/none), tag | PK (campaign_id, kind, tag) | — |
| campaign_resources | campaign_id, resource_id | PK пара | — |
| streams | campaign_id, id, code_norm, name, status | unique (campaign_id, code_norm) | — |
| tasks | stream_id, id, code_norm, name, description, goal, period, stream_points, status | unique (stream_id, code_norm); goal 1..10⁹; points 0..10⁹ | — |
| task_reward_items | task_id, resource_id, amount | PK (task_id, resource_id); amount 1..10⁹ | — |
| milestones | stream_id, id, threshold, achievement_id | unique (stream_id, threshold, achievement_id); threshold > 0 | — |
| challenges | campaign_id, stream_id, id, starts_at, ends_at, finalized_at | интервал внутри сроков кампании — use case; кратность не ограничена (AMB-14) | (campaign_id), (stream_id) |
| budgets | campaign_id, resource_id, allocated_total, spent_total, returned_total | PK пара; CHECK `allocated_total − spent_total + returned_total ≥ 0` | — |
| budget_allocations* | см. operations, kind = BudgetAllocation | — | — |
| operations | company_id, id, kind, result, refusal_code, initiator_type, initiator_master_id, initiator_subject, master_id, campaign_id, purchase_system_id, original_operation_id, reason, source_number, created_at | unique (company_id, initiator_key, kind, source_number); unique partial `(original_operation_id) WHERE kind IN (AwardReversal, SpendReversal) AND result = 'Posted'` (B25.4) | (company_id, master_id, created_at desc), (company_id, campaign_id, kind, created_at desc), (original_operation_id) |
| operation_items | operation_id, resource_id, amount | PK (operation_id, resource_id); amount > 0 | (resource_id) |
| wallets | company_id, master_id, id | unique (company_id, master_id); создаётся в одной транзакции с профилем | — |
| wallet_balances | wallet_id, resource_id, balance | PK пара; CHECK `balance ≥ 0` | — |
| progress_events | company_id, source_subject, event_number, id, master_id, task_id, transmitted_delta, credited_delta, result, reject_reason, accepted_at | unique (company_id, source_subject, event_number); невалидные по данным события не сохраняются вовсе (B16.5, номер свободен) | (task_id, accepted_at), (company_id, master_id, accepted_at desc) |
| progress_state | company_id, master_id, task_id, period_start, current | PK 4 поля; CHECK current ≥ 0 | — |
| completions | company_id, master_id, task_id, period_start, id, stream_points, award_operation_id, reward_outcome, completed_at | unique (company_id, master_id, task_id, period_start) — одно завершение на период (B15.1) | (task_id, completed_at) |
| achievement_grants | company_id, master_id, achievement_id, season, granted_at | PK 4 поля — не более одного раза за сезон (B30.2) | — |
| challenge_scores | challenge_id, master_id, score | PK пара; score ≥ 0 | — |
| challenge_results | challenge_id, master_id, score, place | PK пара; пишется один раз при финализации, пути UPDATE нет (B33.3) | — |
| integration_grants | company_id, id, subject, kind(progress/award/spend), campaign_id, resource_id, purchase_system_id, status, granted_by, created_at, revoked_at | ровно одна цель у grant'а соответствующего вида | (company_id, subject) |
| purchase_systems | company_id, id, code, name, status | unique (company_id, code) | — |
| purchase_system_resources | system_id, resource_id | PK пара | — |
| export_requests | company_id, id, requested_by, scope, target_master_id, resource_id, from_utc, to_utc, status, frozen_at, ready_at, size_bytes, checksum, error_detail, created_at | статус Pending/Forming/Ready/Error/Deleted | (company_id, requested_by, created_at desc) |
| export_operation_ids | export_id, operation_id | PK пара — материализованный снимок B36 | (export_id) |
| download_links | id, export_id, expires_at, created_at, idem_key | expires ≤ created + 60 с | (export_id) |
| idempotency_keys | company_id, initiator_key, operation, target_id, key, response_status, response_body, created_at | PK составной; хранение ≥ 24 ч (очистка фоновая) | — |
| outbox_jobs | id, type, payload, available_at, attempts, status, locked_by, locked_until | два worker'а: `FOR UPDATE SKIP LOCKED` | (status, available_at) |
| audit_records | company_id, id, actor_type, actor_master_id/subject, action, entity_type, entity_id, changes[{field, from, to}], created_at | append-only | (company_id, created_at desc), (entity_type, entity_id) |

\*) выделения бюджета — операции kind BudgetAllocation (единая книга, B18.3).

### 2.2 Бюджеты по ресурсам (отдельно)

- Единица учёта — строка `budgets` на пару «кампания × ресурс» (B08.6). `allocated_total` растёт только операциями BudgetAllocation (инициатор — администратор, B18.1); каждая операция — строка в книге с номером источника (B24.1).
- Инвариант доступного остатка `allocated − spent + returned ≥ 0` обеспечивается тремя уровнями: (1) `SELECT … FOR UPDATE` строки бюджета в транзакции начисления; (2) атомарное обновление счётчиков тем же UPDATE; (3) CHECK-ограничение как последний барьер (T06 — не память процесса). Уменьшение бюджета и перенос между кампаниями отсутствуют (B18.5).
- Траты и возвраты трат бюджет не меняют (B23.6, B27.3); отмены начислений увеличивают `returned_total` только для проведённых начислений этой кампании (B19.1, B26.2).

### 2.3 Единый кошелёк и движения (отдельно)

- `wallets` 1:1 с профилем с момента создания, смена владельца/удаление невозможны — нет ни endpoint'ов, ни путей записи (B01.5, B22.5).
- Баланс по ресурсу — одна строка `wallet_balances` независимо от кампании-источника (B22.2); поступления из всех кампаний суммируются; CHECK `balance ≥ 0` + блокировка строки при трате/возврате отмены (B22.4).
- Движение = строка `operation_items` операции с `result = Posted`; вид операции задаёт знак: TaskReward/ManualAward/SpendReversal — кредит кошелька; Spend/AwardReversal — дебет. Отклонённые операции хранят попытленные позиции, но в балансы и CSV не входят (B28.2, T07).
- Книга операций едина для всех видов (B24/B25/B28): у каждой строки номер источника, инициатор, результат; коррекция — только новая операция отмены со ссылкой на оригинал; частичных отмен нет; одна успешная отмена на оригинал (частичный уникальный индекс).

### 2.4 Удаление и история

| Данные | Правило |
|---|---|
| Опубликованные кампании/стримы/задания, ресурсы, достижения, сотрудники | Физического удаления нет: архив/блокировка; черновик кампании удаляется целиком с дочерними (B09.8) |
| operations, operation_items, progress_events, completions, achievement_grants, challenge_results, audit_records | Append-only; коррекция — операцией отмены (B25.1) |
| export_requests | Мягкое состояние Deleted; объект S3 и незавершённые загрузки чистятся ≤ 10 мин после восстановления зависимостей (T07) |
| download_links, idempotency_keys | Истечение срока / TTL ≥ 24 ч, фоновая очистка |

---

## 3. Инварианты: B-ID → где обеспечивается → конкурентный/сбойный сценарий → как проверить

| Инвариант | Где обеспечивается | Конкурентный/сбойный сценарий | Как проверить |
|---|---|---|---|
| Пакетная награда целиком или ничего: B12.3, B17.1, B20.1–B20.4 | Use case завершения: одна транзакция — прогресс, завершение, очки, достижения, решение; все строки `budgets` пакета блокируются в порядке id ресурса; одна операция TaskReward Posted со всеми позициями либо Declined | Награда 8A+5B при B=1: ни одного движения, завершение/очки/рейтинг сохранены; повтор события после пополнения — прежний результат (SCN-02) | Functional: балансы/бюджеты/операции после отказа; unit: матрица решения; мутация «убрать один item из вставки» роняет тест |
| Последнее доступное средство: B19.2–B19.4, B20.6 | `FOR UPDATE` строки бюджета + CHECK; транзакция отклоняется целиком при нехватке | Бюджет 10, две параллельные награды по 10 → одна Posted, одна Declined, остаток 0, не −10 (E04) | Параллельный functional-тест (2 потока, реальный PG); сценарий «два конкурента» в нагрузке (T09) |
| Повтор после commit: B16.2, B24.2, T05, T06 | Уникальность (company, initiator, kind, number) / (company, source, event_number) в PG, вне кеша; конфликт → возврат сохранённого результата | Ответ потерян, процесс рестартовал, повтор пришёл → те же ID и результат, эффект не дублируется | E2E: kill API между commit и ответом, повтор после рестарта; HTTP-тест replay |
| Конфликт номера: B16.3, B24.3 | Пред-insert проверка + unique violation → 409, ничего не создаётся | Тот же номер с другой суммой/получателем | Contract-тест: 409 + отсутствие записи |
| Отмена: B25.2–B25.4, B26.2–B26.3, B27.1–B27.5 | Unique partial index «одна Posted-отмена на оригинал»; отмена награды блокирует все `wallet_balances` пакета, нехватка одного баланса → Declined целиком (E08); возврат трат бюджет не трогает | Два одновременных запроса отмены одной траты → один Posted +7 (E07); отмена при частично потраченной награде → Declined без возврата A | Параллельный functional-тест; persistence-тест существования частичного индекса |
| Смена прав: B05.1–B05.2, B16.4 | Активность/grant/владелец читаются в той же транзакции из PG (права не из кеша — T06); блокировка запрещает новые начисления/траты, но не административную отмену | Блокировка между отправкой и обработкой: новая трата Declined, повтор старого события Rejected, прежние результаты не аннулируются; отзыв grant → 403/Rejected | Functional с блокировкой посреди потока; contract-тест из перечня 03_AUTH (replay после отзыва) |
| Границы соревнования: B13.4, B31.2, B33.1–B33.5 | `accepted_at` = момент принятия в транзакции; приращение `challenge_scores` только при `accepted_at < ends_at`; финализация берёт `pg_advisory_xact_lock(challenge)`, его же берёт транзакция принятия (короткий захват) до вычисления момента → поздние commit не теряются и не попадают в новый интервал | Событие за 1 мс до конца — в счёте; принятое после конца — нет; повтор на следующий день не переносится (B33.5); финал ≤ 5 с после конца (T07) | Unit-календарь [start,end); functional с TimeProvider на границе; e2e тайминг финализации |
| Снимок/публикация/удаление выписки: B36, B37, T07 | Переход Pending→Forming одной транзакцией материализует `export_operation_ids` — позднее commit не расширяет снимок; повтор формирования читает только снимок; Ready = условный `UPDATE … WHERE status='Forming'` + проверенная контрольная сумма S3; удаление сразу запрещает новые ссылки (409), выданные живут ≤ 60 с; worker не «воскрешает» удалённое | Движение между заморозкой и формированием не входит в повтор (SCN-07); crash между загрузкой файла и Ready — восстановление по контрольной сумме; гонка удаления и worker → статус Deleted остаётся | E2E chaos: S3 off 30 с, повтор задачи; функциональный тест фиксированного снимка; тест гонки удаления |
| Мин-кап и одно завершение: B14.4, B15.1, B15.3 | Блокировка `progress_state`, новое значение = min(goal, current+delta); unique завершения на период; события после цели хранятся с credited 0 | Цель 3: 2+5 → 3/зачтено 1; новое +1 → зачтено 0 (E03); укороченный первый период с полной целью (EDGE-01) | Unit + functional, ожидания из B-правил |
| Достижение раз в сезон: B29.3, B30.2 | PK `achievement_grants` | Одна награда пересекает две вехи одного достижения → одна выдача (EDGE-02) | Functional |
| Кошелёк не отрицателен: B22.4, E05 | CHECK + блокировка строки баланса | Две параллельные траты по 7 при балансе 10 → одна Posted, остаток 3 | Параллельный functional + нагрузочный сценарий |
| Периоды/сезоны/таймзона: B10, B13 | Календарь в Domain (NodaTime, Europe/Tallinn), TimeProvider | Границы дня/месяца/квартала/полугодия/года, смена сезона, DST 25.10.2026 | Unit-тесты календаря на зафиксированном времени |
| Изоляция компаний: B02, E12 | `company_id` из токена во всех запросах и запросах к store; чужое → 404 | masterId=123 в двух компаниях: разные кошельки; подмена companyId в токе | Contract-тесты с двумя компаниями |

---

## 4. API

Полный контракт: `docs/openapi.yaml` (OpenAPI 3.1, базовый путь `/api/v1`, единственная версия). Ниже — соглашения и карта; состав согласован с YAML.

### 4.1 Соглашения

| Аспект | Решение |
|---|---|
| Аутентификация | `Authorization: Bearer` JWT по 03_AUTH (HS256, iss/aud, claims `companyId/actorType/masterId/role`); duplicate identity-claims отклоняются |
| Статусы | 201+Location (создание; экономический отказ — тоже 201 со статусом Declined, T04), 200 чтение, 204 удаление, 400/401/403/404/409/412/428/503 — Problem Details (RFC 9457) с постоянным `code` и `traceId` |
| Повторы | Бизнес-номера (события B16, операции B24) — бессрочная уникальность в PG, повтор возвращает исходное представление; остальные создающие POST — `Idempotency-Key` ≥ 24 ч (scope: company + инициатор + операция/цель + ключ); изменённые данные — 409; конкурентный повтор — 503/Retry-After или ограниченное ожидание |
| Конкурентные правки настроек | Strong ETag на GET; `If-Match` на PUT/PATCH/DELETE настроек: нет — 428, устарел — 412; экономические операции и удаление выписки If-Match не используют (T04) |
| Списки | `items + nextCursor`, сортировка устойчивая (по умолчанию `createdAt, id`; каталоги — по коду), limit 1..100, по умолчанию 50; keyset-курсор не пропускает и не дублирует существовавшие записи |
| Живой рейтинг | `GET /challenges/{id}/leaderboard` — top N (limit), `state: Live\|Final`, `asOfUtc`; собственное место — отдельный `GET …/leaderboard/me` |
| Время/числа | Входные даты — UTC offset; выход — UTC; целые с проверкой переполнения (превышение лимитов — 400 без частичных эффектов) |

### 4.2 Коды ошибок (Problem Details `code`)

| HTTP | code | Случай |
|---|---|---|
| 400 | `validation.failed` / `validation.overflow` / `validation.code-format` | Поля, форматы, пределы чисел |
| 401 | `auth.invalid-token` | Подпись, iss/aud, срок, обязательные claims |
| 403 | `authz.forbidden` / `authz.grant-missing` | Роль/владение/grant/аудитория |
| 404 | `not-found` | Чужой или несуществующий объект компании |
| 409 | `conflict.business-number` / `conflict.idempotency-data` / `conflict.state` / `conflict.publish-check` | Номер с иными данными; неизменяемые поля; публикация не прошла проверку; удаление не-черновика |
| 412 / 428 | `precondition.failed` / `precondition.required` | If-Match |
| 424 | `dependency.unavailable` | Выдача ссылки на скачивание при временном отказе S3 |
| 503 | `service.unavailable` | PostgreSQL недоступен (ограниченный по времени), Retry-After |

Экономические отказы (не HTTP-ошибки, 201 + `result: Declined`): `InsufficientBudget`, `InsufficientFunds` (трата), `InsufficientBalance` (отмена награды), `ResourceUnavailable` (архивный ресурс), `RecipientNotActive` (получатель заблокирован), `OriginalAlreadyReversed`. Отказы событий — `result: Rejected` с `rejectReason` (EmployeeNotActive, AudienceMismatch, CampaignClosed, TargetArchived) по умолчанию занимают номер (AMB-08).

### 4.3 Карта ресурсов (метод → права)

| Ресурс | Методы | Права |
|---|---|---|
| /employees, /employees/{masterId} | GET, POST, PATCH | Admin (GET своего профиля — сотрудник) |
| /resources, /resources/{id} | GET, POST, PATCH | Catalog GET — все компании-участники; остальное Admin |
| /achievements, /achievements/{id} | GET, POST, PATCH | GET — все; запись Admin |
| /campaigns, /campaigns/{id} | GET, POST, PATCH, DELETE | GET — сотрудник (опубликованные текущего сезона), владелец, Admin; создание/настройка — Admin (назначает владельца) и владелец своей; DELETE — только черновик |
| /campaigns/{id}/streams, /streams/{id}, /streams/{id}/tasks, /tasks/{id}, /streams/{id}/milestones, /campaigns/{id}/challenges, /challenges/{id} | GET, POST, PATCH, DELETE | Владелец кампании/Admin; после публикации — только статус-архивация (409 на прочие правки) |
| /campaigns/{id}/budgets, /campaigns/{id}/budget-allocations | GET, POST | GET — владелец/Admin; выделение — Admin |
| /integration-grants, /integration-grants/{id} | GET, POST, DELETE | Admin |
| /purchase-systems, /purchase-systems/{id} | GET, POST, PATCH | Admin |
| /progress-events | POST, GET | POST — интеграция с grant Progress на кампанию; GET — Admin |
| /spends | POST | Сотрудник (свой кошелёк) или система покупок с grant Spend |
| /campaigns/{id}/manual-awards | POST | Владелец/Admin; интеграция с grant Award |
| /reversals | POST | Admin; владелец кампании-источника (начисления); система покупок (свои траты, действующий grant) |
| /me/wallet, /me/operations, /me/achievements, /me/campaigns/{id}/progress | GET | Сотрудник (свои) |
| /employees/{masterId}/wallet, /employees/{masterId}/operations | GET | Admin |
| /operations/{id} | GET | Инициатор, задействованный сотрудник, Admin, система покупок (свои) |
| /campaigns/{id}/operations | GET | Владелец (расходы кампании), Admin |
| /challenges/{id}/leaderboard, …/leaderboard/me | GET | Текущая аудитория; после финала — участники (наличие своего результата), владелец, Admin (AMB-06 — окно до конца сезона: по умолчанию аудитория) |
| /audit-records | GET | Admin |
| /exports, /exports/{id}, /exports/{id}/download-links | POST, GET, DELETE | Сотрудник — свои; Admin — сотрудник/компания своей фирмы; DELETE — заказчик |

Создания без бизнес-номера требуют `Idempotency-Key` (настройки, exports, download-links); progress-events, spends, manual-awards, reversals, budget-allocations защищены собственными номерами источников.

---

## 5. Короткие решения

**Аутентификация/авторизация.** JwtBearer HS256, ключ — декодированные Base64-байты из `Auth__SigningKeyBase64`, явный маппинг claims (companyId, actorType, masterId, role). Роль Admin — только из токена (T03); владение кампаниями, grants, активность, аудитория — данные приложения, проверяются сервисом бизнес-авторизации в Application в транзакции операции (не из кеша, T06). Service-токен не имеет кошелька; masterId из пути/тела никогда не подменяет субъект. Bootstrap: одна доверенная команда запуска `bootstrap` в Api (создание компании и активного администратора через use case), остальное — штатным API.

**Кеш.** Valkey: каталог (30 с) и live-рейтинг (5 с), чистый TTL без инвалидации — допуски отставания делают её излишней; каждый ответ несёт `asOfUtc`; просроченное/повреждённое значение — miss; поздний fill не продлевает снимок; при отказе — ограниченное ожидание (~200 мс) и чтение из PostgreSQL. Права, балансы, экономические решения, лимиты выполнения из кеша не берутся (T06).

**Фоновые операции.** Outbox в PostgreSQL (`FOR UPDATE SKIP LOCKED`, два worker'а): FinalizeChallenge (доступна с `ends_at`, поллинг 1 с — укладывается в SLO 5 с), ExportFormation (повторы с backoff, восстановление после crash по контрольной сумме S3), Cleanup (ссылки, объекты удалённых выписок ≤ 10 мин, idempotency ≥ 24 ч). Задача ставится в той же транзакции, что и бизнес-изменение.

**Отказы.** PostgreSQL недоступен → ограниченный по времени 503 + Retry-After, readiness «не готов» к основным операциям, liveness не падает; Valkey недоступен → деградация latency, работа из PG; S3 недоступен → основной учёт продолжается, задачи экспорта повторяются. Структурированные логи + OpenTelemetry, correlation/traceId в ProblemDetails; токены, ключи и signed URL не логируются.

**Нагрузка.** Профиль T09: 85% чтений (каталог/кошелёк/рейтинг) — Valkey + индексированные точечные чтения; 15% записей сериализуются на горячих строках (бюджет/баланс) короткими транзакциями. Пулы соединений Npgsql ограничены под 1 GiB на процесс; UUID v7 для локальности индексов. Целевые p95: чтение ≤ 200 мс, запись ≤ 500 мс; проверка итоговых бизнес-состояний, не только HTTP-кодов. Это условия эксперимента, не SLA.

**Слои тестов** (по T08): Unit — правила Domain (календарь/DST, min-кап, места 1-1-3, переполнения); Functional integration — реальные PG/Valkey/S3 через use cases и HTTP без DbContext в arrange; HTTP/contract — реальные подписанные JWT, статусы, ETag, ProblemDetails, пагинация; Persistence/migrations — маппинги, уникальные/частичные индексы, CHECK, пустая БД и пошаговые миграции; Architecture — зависимости (нарушение сборки падает); E2E — отдельные процессы, конкуренция, рестарты, отключения Valkey/S3/API, неизменяемость снимка. Ожидания выводятся из B-правил (SCN/EDGE), не из вывода приложения.

### 5.1 Ключевые спорные решения (вариант → причина → цена)

| Решение | Причина выбора | Цена/ограничение |
|---|---|---|
| Пессимистичные блокировки строк бюджета/баланса вместо serializable | Детерминированность на горячих строках, PG-only (T01), меньше повторов | Конкурентные начисления в один бюджет выстраиваются в очередь; для профиля записи (15 RPS) приемлемо |
| Единая книга operations вместо таблиц на вид | B28.2 «история объясняет каждый баланс», один экспорт-путь CSV | Широкая схема с явными nullable-полями (допустимо по T04) |
| Outbox в PostgreSQL, не внешний брокер | T01 не предоставляет брокера; атомарность с бизнес-commit | Поллинг 1 с; SLO 5 с достигается |
| Материализованный снимок выписки (множество id операций) | T07 прямо запрещает заменять снимок фильтром по timestamp (гонка позднего commit) | До ~10⁵ строк на выписку, чистятся вместе с ней |
| NodaTime для календаря | Корректная арифметика периодов/сезонов и DST в Europe/Tallinn | Одна зависимость в Domain |
| Declined как 201-ресурс | Требование T04; единая схема Posted/Declined | Клиент обязан читать `result`, 201 ≠ проведение |
| Advisory-lock протокол финализации челленджа | Закрывает гонку «принятие в момент конца интервала» без задержки финала | Короткий лок на каждое событие в активном челлендже |
| Отказы событий занимают номер (по умолчанию, AMB-08) | Симметрия с B24 («результат, включая отказ») и защита от повторной обработки | Смена поведения после решения заказчика — миграция не нужна, правило в одном месте |
| Кеш по TTL без инвалидации | Допуски 30/5 с покрывают устаревание конструктивно | Окно устаревания принципиально не меньше допуска |

---

## 6. Покрытие

### 6.1 B01–B37 → механизм

| B | Механизм (кратко) |
|---|---|
| B01 | /employees POST: профиль+кошелёк одной транзакцией; уникальность masterId |
| B02 | company_id из токена во всех store/запросах; чужое — 404 |
| B03 | Матрица прав 4.3 + сервис бизнес-авторизации в Application |
| B04 | /integration-grants (+ ревок), проверка гранта в транзакции операции |
| B05 | Проверки активности/гранта/владельца в момент операции (инвариант «Смена прав») |
| B06 | /resources: нормализованный код, уникальность, неизменяемость, нулевые балансы |
| B07 | Архивация PATCH статуса; Declined `ResourceUnavailable`; отмена после архивирования |
| B08 | Уникальные индексы кодов на уровнях; campaign_resources; budgets на пару |
| B09 | PATCH status → Published (проверка состава, 409 publish-check), неизменяемые поля (409), DELETE черновика |
| B10 | Календарь Domain; кампания в сезоне; новые кампании через POST |
| B11 | Движок тегов any/all/none; прогресс только активным в аудитории; /me/campaigns |
| B12 | Валидация заданий и reward items (уникальность ресурса, сумма > 0) |
| B13 | Календарь периодов Domain (NodaTime), TimeProvider |
| B14 | Use case принятия: min(goal, cur+delta), передано/зачтено, момент принятия |
| B15 | unique завершений; события после цели с credited 0 |
| B16 | Уникальность (company, source, event_number); повтор → сохранённый результат; 409 на расхождение |
| B17 | Одна транзакция use case; отказ бюджета — Declined, не 5xx |
| B18 | /budget-allocations (Admin), история = операции |
| B19 | Счётчики budgets + CHECK; атомарность бюджет↔баланс |
| B20 | Решение о награде в транзакции завершения; финальность |
| B21 | /manual-awards (владелец/Admin/grant Award); не меняет прогресс |
| B22 | /me/wallet; один баланс на ресурс; CHECK ≥ 0 |
| B23 | /spends; проверки активности владельца; отказы 201 Declined |
| B24 | Номера операций, уникальность, 409 |
| B25 | Append-only книга; отмены как операции; частичный индекс |
| B26 | /reversals для начислений (балансы пакета или Declined) |
| B27 | /reversals для трат (Admin/система покупок, grant действующ) |
| B28 | Схема Operation; /audit-records |
| B29 | Очки стрима в completions; вехи → достижения |
| B30 | PK achievement_grants |
| B31 | Настройка челленджа до публикации; счёт из зачтённых дельт |
| B32 | Leaderboard: dense rank 1-1-3, сортировка masterId, top N + me, состав данных |
| B33 | FinalizeChallenge job, immutable challenge_results |
| B34 | /me/*, /campaigns/{id}/operations, Admin-представления, пагинация/фильтры |
| B35 | /exports (scope, ресурс/все, [from,to)) |
| B36 | Статусы выписки; материализованный снимок |
| B37 | DELETE выписки, ссылки ≤ 60 с, изоляция сбоев |

### 6.2 T01–T10 → как выполнено

| T | Как |
|---|---|
| T01 | Стек зафиксирован; версии SDK/образов фиксируются перед исполнением (этап 3); миграции только под PG 17 |
| T02 | Проекты/зависимости §1, архитектурные тесты |
| T03 | JWT §5, bootstrap-команда, без login endpoints |
| T04 | REST §4 + openapi.yaml; таблицы покрытия 6.1 |
| T05 | Двухуровневая идемпотентность §4.1; TimeProvider |
| T06 | PG-инварианты §3, правила кеша §5, 2 API + 2 worker |
| T07 | Пайплайн экспорта §3 (снимок, Ready, ссылки, очистка) |
| T08 | Слои тестов §5 |
| T09 | Подход к нагрузке §5; сценарий «два конкурента» в §3 |
| T10 | Compose/README/примеры запросов — план этапа 3 (в объёме стадии не реализуется) |

---

## 7. Противоречия и сопоставления входов

Прямых противоречий между B-, T- и JWT-входами не выявлено. Сопоставления, требующие фиксации (не правки тайком):

| # | Место | Сопоставление |
|---|---|---|
| С1 | B35.3 ↔ T07 | «Вид и номер операции» в CSV реализованы как `kind` и `operationId` (внутренний ID); внешний номер источника в CSV не входит — header зафиксирован T07; внешний номер доступен в API операций |
| С2 | B32.3 ↔ T04 | «Первые N» реализовано параметром `limit` (по умолчанию 50, максимум 100) из правил списков T04; фактическое значение N — AMB-05 |
| С3 | AMB-07 ↔ T07 | Требование «финальный рейтинг ≤ 5 с после завершения» закрывает вопрос триггера: финализация автоматическая в момент конца интервала (outbox-задача) |
| С4 | AMB-13 ↔ T07 | Срок разрешения на скачивание зафиксирован: 60 секунд |

## 8. Открытые вопросы

Для не закрытых входами вопросов stage-1 выбраны минимальные умолчания (помечены); смена решения заказчиком не требует переделки контракта, кроме отмеченного.

| ID | Умолчание этапа 2 | Статус |
|---|---|---|
| AMB-02 | Владельцу разрешена отмена начисления заблокированному (отмена — не «новое начисление/трата») | ждёт заказчика |
| AMB-04 | «Участник» = активный сотрудник текущей аудитории кампании | ждёт заказчика |
| AMB-05 | N = limit списка (50/100) | ждёт заказчика |
| AMB-06 | В окне «конец кампании — конец сезона» рейтинг читает текущая аудитория | ждёт заказчика |
| AMB-08 | Отказы событий (кроме неверных данных) занимают номер, повтор возвращает сохранённый отказ | ждёт заказчика |
| AMB-09 | Повтор принятого события при потере аудитории возвращает сохранённый результат (аудитория не перепроверяется) | ждёт заказчика |
| AMB-11 | «Успешное начало формирования» = транзакция перехода в Forming, она же фиксирует снимок | ждёт заказчика |
| AMB-12 | Состав проверки публикации зафиксирован: поля заданий, награды из ресурсов кампании, вехи/достижения компании, челлендж в сроках, аудитория, сезон | ждёт заказчика |
| AMB-14 | Несколько челленджей у кампании разрешены (ограничения нет) | ждёт заказчика |
| Техн. | Выбор локального S3-образа и фиксация версий SDK/пакетов — перед исполнением этапа 3 (T01) | этап 3 |

Стадия 2 завершена: спроектированы границы модулей, модель данных, инварианты, API (openapi.yaml) и решения; к планированию/реализации не перехожу до утверждения.
