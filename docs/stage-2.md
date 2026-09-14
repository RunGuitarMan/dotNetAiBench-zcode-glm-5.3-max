# Motiva — Stage 2. Архитектура и API (внешняя нормализация R2)

Статус входа: `EXTERNALLY_NORMALIZED`, ожидает независимой проверки для G3. Авторский Gate G2 — **НЕ ПРОЙДЕН**. Реализация, миграции и runtime-проверки не выполнялись; проверка документов — §7.1.
Входы: `docs/01_BUSINESS.md` v3.0 (B01–B37, E01–E12), `docs/02_ENGINEERING.md` v3.0 (T01–T10), `docs/03_AUTH.md` + `auth/jwt.py`, `.editorconfig`, утверждённый `docs/stage-1.md`. Предыдущие авторские сдачи сохранены в Git (a847d7d — первая сдача, 2a345c4 — r1, fb3bc35 — r2).
Контракт: `docs/openapi.yaml` (OpenAPI 3.0.3, валидация — §7.1).

### Служебная запись внешней нормализации

Исходная финальная авторская сдача: `fb3bc35b9c8128a0424e5dfba16396a9a1bd1673`. Перед изменениями HEAD совпадал с ней; tracked-файлы чистые, untracked `.DS_Store` сохранён. Авторский этап 2 закрыт после двух итераций правок с непройденным G2. Изменения ниже подготовлены внешним агентом для последующей независимой проверки входа G3; это не третья авторская итерация. Оценки и результат участника не меняются; прохождение независимого gate не утверждается.

Содержательные и локальные согласующие правки (пункты — поручение организатора):

| Дефект/уточнение → требование | Изменённые места | Минимальное изменение смысла |
|---|---|---|
| П.1, A2-03/R2-04 → B02/B04/B05/B23/B27, T04 | §3.3, §4.3, B04 в §6.1; OpenAPI GET /me/operations и /operations/{id} | История трат/возвратов по реально выданным парам system/resource своей компании, независимо от инициатора; текущие права для каждого чтения, 403 списка; DTO и области номеров сохранены |
| П.2, H0 Q01/Q03 → B05/B21/B26/B27 | §3.3, §7.4, AMB-02/03/04 в §8; OpenAPI manual-awards/reversals | Подтверждены активная аудитория без прежнего прогресса и только Admin-коррекция заблокированного получателя, включая replay |
| П.2, H0 Q04/Q08 → B05/B16/B17/B24, T04/T05 | §3.0, §3.3, §4.2, §7.4, AMB-08/09; OpenAPI POST progress-events и rejectReason, общие повторы | Активность получателя progress до номера для нового обращения/replay; 403 без события; сохранённый отказ занимает номер, техническая неудача без commit — нет; enum сохранён |
| П.2, H0 Q02/Q04/Q05/Q09 → B07/B09–B13/B18/B20/B26/B32/B35/B36 | §7.4, AMB-06; локальные descriptions OpenAPI reward/leaderboard/campaign/export/budget | Приоритет недоступности ресурса пакета; сезонная видимость рейтинга; полуинтервалы; разрешённые сроки выделений и сохранение старой истории |
| П.2, H0 Q06/Q07 → B01/B06/B08/B12/B14/B29/B31/B32, T05 | §7.4; локальные descriptions/ограничения существующих полей OpenAPI | Целые и допустимые границы; числовой masterId; крайние обычные пробелы кодов, регистр и точные внешние номера |
| П.2, старые AMB → B31 и текущие решения §8 | §8 | Закрытые соответствия для чтения неизменённого stage-1; у коллекции челленджей нет дополнительного верхнего лимита |
| П.3 → T04/T10 | Ссылки экспорта на §3.6; шапка/общее правило списков OpenAPI | Убраны неверные ссылки и H1-статус; отражено существующее исключение live top N + собственное место |
| Дополнительное доказуемое противоречие, ограниченная сверка п.3 → B19/B20.6 | §3.1, строка события с наградой | «Исход не зависит от порядка» заменено на сохранение целостности при любом порядке: при бюджете 10 и двух наградах по 10 порядок определяет получателя Posted, но перерасхода нет |

Механический перенос из единственного источника `git show 2a345c4:docs/stage-2.md` (r1), отдельно от новых уточнений:

| Исходный раздел r1 | Возвращённый фрагмент текущего документа |
|---|---|
| §1.1–1.3 | Обязанности проектов, направленные зависимости, архитектурные проверки, порты и причины их введения (§1) |
| §4.1–4.2 | Недостающие соглашения и HTTP/code-таблица (§4.1–4.2); порядок R2/H0 остаётся единственным |
| §4.3 | Недостающие семейства API-матрицы (§4.3); действующие маршруты/права R2 и п.1–2 сохранены |
| §5 | Auth с чтением владения/grants/активности в транзакции; прежние детали кеша, outbox, отказов, нагрузки и слоёв тестов (§3.7, §5) |
| §5.1 | Существовавшие обоснования спорных решений (§5.1); ссылки на все замки стрима/SLO и верхнюю границу свежести синхронизированы с R2 §3.2/§3.7, старые варианты не возвращены |
| §6.1–6.2 | Отсутствующие B/T-строки покрытия (§6), без замены строк R2 |
| §7 | Сопоставления входов С1–С4 (§7.3) |

Объём строк увеличен восстановлением уже существовавшего текста. Новые сущности, поля DTO, индексы, порты, блокировки и бизнес-умолчания внешним агентом не вводились.

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

Соглашения: PostgreSQL 17; `company_id` на каждой строке (тенант из проверенного токена); UUID v7; `timestamptz` UTC; суммы `bigint` (баланс/бюджет ≤ 10¹⁵, цели/дельты/очки ≤ 10⁹); коды нормализуются в верхний регистр; внешние номера 1–100 ASCII регистрозависимы. EF-сущности ≠ DTO. Уровень изоляции всех бизнес-транзакций — READ COMMITTED (единственное намеренное исключение — материализация снимка экспорта, §3.6).

### 2.1 Таблицы (изменения R2 выделены в тексте)

| Таблица | Существенные поля | Ключи / ограничения |
|---|---|---|
| companies | id, name, time_zone | PK id |
| employees | company_id, master_id, is_active, created_at | PK (company_id, master_id) |
| employee_tags | company_id, master_id, tag | PK (company_id, master_id, tag) |
| resources | company_id, id, code_norm, name, status | unique (company_id, code_norm) |
| achievements | company_id, id, code_norm, name, description | unique (company_id, code_norm) |
| campaigns | company_id, id, code_norm, season, owner_master_id, starts_at, ends_at, status (Draft/Published/Archived/**Deleted**), published_at, **version int ≥ 1** | unique (company_id, season, code_norm) — только не-Deleted строки; version — источник strong ETag агрегата (§4.4) |
| campaign_tags / campaign_resources | campaign_id, kind+tag / resource_id | PK по колонкам |
| streams | campaign_id, id, code_norm, name, status | unique (campaign_id, code_norm) |
| tasks | stream_id, id, code_norm, name, description, goal, period, stream_points, status | unique (stream_id, code_norm) |
| task_tags | task_id, kind, tag | PK (task_id, kind, tag) — аудитория задания (B11) |
| task_reward_items | task_id, resource_id, amount | PK (task_id, resource_id) |
| milestones | stream_id, id, threshold, achievement_id | unique (stream_id, threshold, achievement_id) |
| challenges | campaign_id, stream_id, id, starts_at, ends_at, finalized_at | интервал внутри сроков кампании; кратность — AMB-14 |
| budgets | campaign_id, resource_id, allocated_total, spent_total, returned_total | PK пара; CHECK `allocated − spent + returned ≥ 0` |
| **operations** | company_id, id, kind, result, refusal_code, initiator_type, initiator_key, master_id, campaign_id, purchase_system_id, original_operation_id, source_number, reason, created_at, **essential_data (канонический JSON существенных данных), response_status, response_body (исходное представление ответа)** | **unique (company_id, initiator_key, kind, source_number) — кампания в ключ НЕ входит**; unique partial `(original_operation_id) WHERE kind IN (AwardReversal, SpendReversal) AND result='Posted'` |
| operation_items | operation_id, resource_id, amount | PK (operation_id, resource_id) |
| wallets / wallet_balances | (company_id, master_id) / balance | unique (company_id, master_id); PK (wallet_id, resource_id); CHECK balance ≥ 0 |
| **progress_events** | company_id, source_subject, event_number, id, master_id, task_id, transmitted_delta, credited_delta, result, reject_reason, accepted_at, **essential_data, response_status, response_body** | **unique (company_id, source_subject, event_number)**; невалидные по данным не сохраняются (B16.5) |
| progress_state | company_id, master_id, task_id, period_start, current | PK 4; current ≥ 0 |
| completions | company_id, master_id, task_id, period_start, id, stream_points, award_operation_id, reward_outcome, completed_at | unique (company_id, master_id, task_id, period_start) |
| stream_points | company_id, master_id, stream_id, season, points | PK 4; points ≥ 0 |
| achievement_grants | company_id, master_id, achievement_id, season, granted_at | PK 4 |
| challenge_scores / challenge_results | (challenge_id, master_id, score) / (…, place) | PK; results пишутся при финализации, UPDATE-пути нет |
| integration_grants | company_id, id, subject, kind, campaign_id, resource_id, purchase_system_id, status, … | CHECK сочетаний: Progress → campaign; Award → campaign+resource; Spend → resource+purchase_system |
| purchase_systems (+resources) | code, name, status, accepted | unique (company_id, code) |
| **export_requests** | company_id, id, requested_by, scope, target_master_id, resource_id, from_utc, to_utc, status (Pending/Forming/Ready/Error/Deleted), **generation int ≥ 0**, frozen_at, snapshot_saved bool, ready_at, **s3_key, s3_version**, size_bytes, checksum, error_detail, created_at, lease_owner, lease_until, **cleanup_intent bool** | один заказ — много попыток; generation — владелец попытки (§3.6) |
| export_operation_ids | export_id, operation_id | PK пара — материализованный снимок B36 |
| download_links | id, export_id, expires_at, created_at, idem_key | expires ≤ created + 60 с |
| idempotency_keys | company_id, initiator_key, operation, target_id, key, response_status, response_body, created_at | PK составной; TTL ≥ 24 ч — только для операций без бизнес-номера |
| outbox_jobs | id, type, payload, available_at, attempts, status, locked_by, locked_until | `FOR UPDATE SKIP LOCKED` + lease |
| audit_records | company_id, id, actor…, action, entity_type, entity_id, changes, created_at | append-only |

### 2.2 Бюджеты, кошелёк, движения

- Бюджет — строка на пару «кампания × ресурс»; ресурс обязан входить в `campaign_resources` (API до публикации, §4.3). `allocated_total` растёт только BudgetAllocation (Admin). Инвариант `allocated − spent + returned ≥ 0` — `FOR UPDATE` + атомарные счётчики + CHECK. Траты и возвраты трат бюджет не меняют (B23.6, B27.3).
- Кошелёк 1:1 с профилем; один баланс на ресурс независимо от кампании-источника; CHECK `balance ≥ 0`.
- **Движение личного кошелька — Posted operation_item только видов TaskReward / ManualAward / Spend / SpendReversal / AwardReversal** (кредит/дебет по виду). **BudgetAllocation хранится в финансовой истории, но личный кошелёк не меняет и в CSV не попадает, включая экспорт по компании** (B28.2, T07). Отклонённые операции хранят попытленные позиции, в балансы и CSV не входят. CSV-контракт T07 неизменен: `operationId,masterId,resourceCode,amount,kind,campaignId,originalOperationId,createdAtUtc`, сортировка createdAtUtc, operationId, resourceCode, диапазон [from,to).

### 2.3 Удаление и история

| Данные | Правило |
|---|---|
| Опубликованные кампании, ресурсы, достижения, сотрудники | Физического удаления нет: архив/блокировка |
| **Черновик кампании с финансовой историей** | DELETE переводит campaigns в **Deleted-надгробие**: настройки (стримы, задания, вехи, челленджи, теги, набор ресурсов) удаляются каскадом; код освобождается (code_norm перезаписывается служебным значением с id); **operations / operation_items / номера / audit сохраняются со ссылкой на надгробие** — финансовая история не каскадится и остаётся объяснимой (B25.1, B28). Бюджетирование черновика не запрещается — история сохраняется, а не предотвращается |
| operations, operation_items, progress_events, completions, stream_points, achievement_grants, challenge_results, audit_records | Append-only / монотонные счётчики; коррекция — операцией отмены |
| export_requests | Мягкое Deleted + cleanup_intent; жизненный цикл — §3.6. **Истечение ссылки или lease саму выписку не удаляет** — такого бизнес-правила нет |
| download_links, idempotency_keys | Истечение срока / TTL ≥ 24 ч |

---

## 3. Инварианты и протоколы

### 3.0 Единый порядок обработки любой операции (R2-01)

1. **Проверенная идентичность и компания**: подпись/iss/aud/claims JWT; `companyId` из токена; сотрудник определяется токеном, masterId из запроса его не переопределяет.
2. **Актуальные полномочия и обязательные проверки активности**: роль, действующий grant, текущее владение, активность инициатора (user) и обязательная активность получателя progress-event — до номера и для нового обращения, и для replay (B16.4; H0 Q04/Q08). Нарушение → 401/403 **без записи и без эффекта**; ошибка доступа никогда не превращается в сохранённый успех/Declined/Rejected.
3. **Бизнес-номер**: уникальность события — (company, проверенный source subject, eventNumber); экономической операции — (company, проверенный initiator, kind, operationNumber); **кампания в ключ не входит** — campaignId (в т.ч. из URL) входит в существенные данные ручного начисления и выделения. Найден номер + совпадают существенные данные → **возврат сохранённого исходного ответа** (`response_status` + `response_body` из §2.1; исходные ID, статус, представление; добайтовая сборка не выполняется); совпадают не все → 409 `conflict.business-number` без нового эффекта. Неопределённость «закоммитился ли прошлый запрос» разрешается повторным поиском сохранённого результата, а не повторным выполнением эффекта.
4. **Только для нового действия** — бизнес-предусловия (аудитория, архив, сроки, бюджет, баланс) и сам эффект.

H0 Q08: ошибки валидации/прав не занимают номер; временная техническая неудача без committed согласованного результата также его не закрепляет. Сохранённый бизнес-отказ занимает номер и воспроизводится; потеря ответа после commit номер не освобождает. Блокировка получателя progress-event на шаге 2 — 403 `authz.employee-not-active` без нового события/занятого номера. Договор нового ручного начисления/сервисной траты с `201 Declined/RecipientNotActive` сохраняется (§3.3).

Существенные данные (порядок JSON-полей и позиций награды не влияет; изменение любого поля при прежнем номере — 409):

| Вид | Существенные данные |
|---|---|
| Событие прогресса | masterId, taskId, переданная дельта |
| Трата | masterId (получатель), purchaseSystemId, resourceId, amount |
| Ручное начисление | campaignId, masterId, resourceId, amount, reason |
| Выделение бюджета | campaignId, resourceId, amount, reason |
| Отмена | originalOperationId, reason |

Обязательный контрпример (E1/E2): событие E1 дало частичный прогресс и ответ с `completion: null`; позже E2 завершил задание. Повтор E1 возвращает сохранённый `response_body` с `completion: null`; подстановка текущего completion из БД или флаг `replay` в ответе исключены — представление идентично исходному. Хранение канонических данных запроса и исходного ответа — **бессрочное**, в самих operations/progress_events; записи idempotency_keys с TTL 24 ч (для POST без бизнес-номера) их не заменяют. Для найденного принятого события период, аудитория, архив, сроки и награда заново не вычисляются (B16.4); отзыв grant/владения и требуемая активность не обходятся (шаг 2).

### 3.1 Единый порядок блокировок (R2-02)

Глобальный порядок захвата (все — до конца транзакции; внутри группы строки берутся по одинаково определённому ключу: advisory — по id челленджа, budgets/wallet_balances — по resource_id, прочие — по PK):

1. исходная операция (только для отмен);
2. advisory-замки челленджей стрима (только для событий; §3.2);
3. строка campaigns `FOR UPDATE` (публикация и изменения её дочерних настроек);
4. progress_state;
5. stream_points;
6. budgets (по resource_id);
7. wallet_balances (по resource_id).

Пути:

| Путь | Транзакция / защищаемые записи | Порядок | Результат конфликта |
|---|---|---|---|
| Событие с завершением/наградой | одна READ COMMITTED; advisory челленджей → progress_state → stream_points → budgets → wallet_balances | §3.2, затем 4–7 | ожидание блокировки; целостность результата сохраняется при любом порядке конкурентов (B19/B20.6) |
| Ручное начисление | budgets → wallet_balances | 6 → 7 | нехватка бюджета → Declined (без изменений остатков) |
| Выделение бюджета | budgets | 6 | нет (только рост) |
| Трата | wallet_balances | 7 | нехватка средств → Declined без изменения баланса |
| Отмена начисления | исходная операция → budgets → wallet_balances (без обратного порядка) | 1 → 6 → 7 | нехватка одного баланса → Declined целиком; второй конкурентный возврат → unique partial index → Declined `OriginalAlreadyReversed` |
| Возврат траты | исходная операция → wallet_balances | 1 → 7 | конкурентный возврат → Declined `OriginalAlreadyReversed` |
| Публикация / изменения настроек | campaigns FOR UPDATE → дочерние строки | 3 | проверка актуального Draft, чтение состава и сохранение согласованы: одновременная публикация и запрещённая правка настройки невозможны |

**Первые агрегаты** (progress_state, wallet_balances, budgets, stream_points, challenge_scores): `SELECT FOR UPDATE` отсутствующей строки создание не защищает. Протокол: `INSERT … ON CONFLICT DO NOTHING` отдельным оператором, затем `SELECT … FOR UPDATE` отдельным оператором; ожидаемые ошибки уникальности не вызываются вовсе — после ошибки PostgreSQL потребовался бы откат всей транзакции.

**Повтор ≠ новое событие**: дедупликация — только по (source, eventNumber). Другое новое событие того же сотрудника/задания/периода сохраняется со своим результатом; при уже существующем completion ему возвращается creditedDelta=0 с собственным записанным ответом, а не результат первого события. Одно completion → один набор очков и одно решение о награде (unique); уникальность achievement_grants защищает от дубля, но пропуск вехи предотвращается совместной проверкой всех пересечённых порогов при обновлении stream_points (§3.2).

**Отказ — не технический откат**: решение Declined/Rejected формируется проверками до обновления счётчиков и фиксируется в той же транзакции; событие, completion, очки и Declined-награда атомарны (B17.3, B20). CHECK-нарушения возможны только из-за дефекта — такая транзакция откатывается целиком и не оставляет половины результата.

### 3.2 acceptedAt и челленджи (R2-03)

Протокол события (после шагов 3.0.1–3.0.3 для нового события):

1. Захватить advisory-замки (`pg_advisory_xact_lock`) **всех челленджей стрима** в устойчивом порядке по возрастанию id — отбор по времени не выполняется, время ещё не зафиксировано.
2. Один раз взять время через TimeProvider и назначить `accepted_at`. Это же время — для сроков кампании, периода задания и проверки каждого челленджа `startsAt ≤ accepted_at < endsAt`.
3. Последующие ожидания (progress/budget/wallet) и задержка HTTP-ответа `accepted_at` не меняют. Timestamp входа HTTP, начала транзакции и PostgreSQL `now()`/`CURRENT_TIMESTAMP` (время начала транзакции) не используются.
4. Счёт каждого подходящего челленджа увеличивается на фактически зачтённую дельту; участие с нулевым зачтением тоже создаёт участника со счётом 0 (B31.4).
5. Все изменения события — одна транзакция; замки челленджей удерживаются до commit.

Финализатор (outbox, доступен с `ends_at`, поллинг 1 с; итог публикуется ≤ 5 с после конца при исправных зависимостях — протокол, а не обещание):

1. Взять тот же advisory-замок челленджа.
2. После захвата проверить, что `endsAt` уже наступил; иначе — отпустить и вернуться по расписанию.
3. **Отдельным запросом после ожидания замка** (READ COMMITTED — не снапшот, взятый до ожидания) прочитать challenge_scores для итога.
4. Атомарно сохранить challenge_results (соревновательное ранжирование 100,100,90 → 1,1,3; равные — по возрастанию masterId) и признак финализации.
5. Повторная финализация состояние не меняет.

Трассы проверки: (а) `accepted_at < endsAt`, commit/ответ задержаны — событие в итоге; (б) запрос пришёл до `endsAt`, но замок получен после — в завершившийся челлендж не входит; (в) `accepted_at = startsAt` входит, `accepted_at = endsAt` не входит; (г) несколько пересекающихся челленджей — каждый получает свою дельту, замки в одном порядке, взаимоблокировки нет; (д) повтор события и отмена награды итог не меняют. REPEATABLE READ остаётся только для материализации снимка экспорта (§3.6).

### 3.3 Матрица прав по состояниям участников (R2-04)

| Ситуация | Исход | Обоснование |
|---|---|---|
| user-инициатор неактивен (любое действие, включая replay) | 403 `authz.employee-not-active`, без записи | B05.1–B05.2 |
| Service: проверенный subject → действующий grant → конкретные purchaseSystemId и resourceId; исходная трата принадлежит разрешённой системе. Subject ≠ purchaseSystemId; владение одной системой не даёт весь кошелёк | иначе 403 без записи (включая replay) | B04, T03 |
| Grant отозван (новое действие и replay) | 403 без записи; прежние результаты не аннулируются | B05.1, B16.4 |
| Бывший владелец после смены | 403; полномочия у текущего владельца | B05.1 |
| Получатель progress-event заблокирован (новое обращение и replay) | 403 `authz.employee-not-active` до номера, без нового события/занятого номера | B05, B16.4; H0 Q04/Q08 |
| Инициатор активен/полномочен, получатель заблокирован, **новое** ручное начисление/сервисная трата | сохраняемый отказ — 201 `Declined` (`RecipientNotActive`); Service ограничения не обходит | B05.2 |
| **Отмена начисления / возврат траты** (коррекция) при заблокированном получателе | H0 Q03: только Admin — разрешено (B05.2, B26.6, B27); владелец без Admin и Service — 403, в том числе перед replay; Admin-коррекция активности получателя не требует | B26–B27 |
| Admin выполняет коррекцию заблокированного кошелька | разрешено; не превращается в разрешение нового начисления/списания | B05.2 |

Общее правило получателя: активность требуется для новых начислений/трат; для progress-event — до номера также при replay (H0 Q04/Q08). При коррекции заблокированного получателя допустим только Admin, включая проверку перед replay (H0 Q03).

Для `GET /me/operations` и `GET /operations/{id}` service читает только историю трат `Spend` и их возвратов `SpendReversal` в проверенной компании. Каждая пара `purchaseSystemId/resourceId` должна целиком соответствовать действующему grant Spend проверенного subject; независимое объединение разрешённых систем и ресурсов запрещено. История включает сохранённые Posted/Declined-результаты независимо от инициатора (сотрудник, сервис или Admin); настоящий initiator остаётся в DTO. Принадлежность возврата определяется исходной тратой через `originalOperationId` и существующие поля/позиции, без новой модели. Начисления, другие операции кошелька, другая компания и пары вне grants сервису недоступны; сам факт инициирования не расширяет эту область (B04/B23/B27).

Текущие права проверяются при каждом чтении, включая продолжение по cursor и известный operationId: прежний cursor/ID не восстанавливает отозванный grant (B05). По T04: неверный токен — 401, отсутствие полномочий на чтение — 403; чужой/недоступный персональный объект при детальном чтении — 404. В списке возвращаются только разрешённые пары; сохранённые фильтры не расширяют область. Сотрудник читает только собственный кошелёк; права владельца/Admin остаются прежними.

### 3.4 Собственная история выполнения (R2-05)

`GET /me/progress-events` (сотрудник из проверенного токена): прошлые периоды и сезоны, частичный прогресс, завершения и решения о награде с причинами отказов; доступ сохраняется после потери аудитории (B11.6) и архива кампании; фильтры from/to/campaignId/taskId, limit 1..100, cursor, устойчивый порядок (acceptedAt, id); история наград с фильтром по ресурсу — `/me/operations` (resourceId). Текущее состояние — `/me/campaigns/{id}/progress` (текущие периоды), историей не подменяется. Чужие данные недоступны (тенант + собственный masterId).

`ProgressEventResult` самодостаточен при `completion: null`: masterId, taskId, periodStartUtc (для принятых), переданная/зачтённая дельты, результат и причина.

Трасса: частичный прогресс в периоде 1 → завершение/награда в периоде 2 → смена периода 3 → потеря аудитории. Сотрудник видит: `/me/progress-events` — событие периода 1 (частичный прогресс) и событие с completion и решением о награде периода 2; `/me/operations` — операции награды; `/me/wallet` — балансы. Владелец видит только `GET /campaigns/{id}/progress` и `/campaigns/{id}/operations` своей кампании — не весь кошелёк.

### 3.5 Кошелёк, экономика, изоляция

Значения G2 без изменений: пакетная награда целиком или ничего (блокируются все бюджеты пакета по resource_id, одна операция Posted со всеми позициями либо Declined, финальность решения B20.3); последнее доступное средство (FOR UPDATE + CHECK, остаток ≥ 0); мин-кап и одно завершение на период; достижения ≤ 1 раза за сезон (PK + совместная проверка вех при обновлении stream_points); баланс ≥ 0; изоляция компаний.

### 3.6 Экспорт: владение попыткой, неизменяемые байты, очистка (R2-06)

Снимок: первый успешно зафиксированный старт — транзакция (REPEATABLE READ) атомарно переводит Pending→Forming, **сохраняет состав export_operation_ids и факт фиксации снимка** в согласованном MVCC-снимке. Поздний commit с ранним timestamp не добавляется. Ошибка до фиксации — снимка нет. Error→Forming при существующем снимке повторно использует его; при отсутствии — выполняется первое успешное формирование. **Истечение lease не даёт права переснять данные** — переснимок невозможен ни при каких состояниях.

Владение попыткой: `generation` (attemptId) в export_requests. Захват/перехват атомарно увеличивает generation. Продление lease, запись прогресса, Error и Ready допускаются **только текущей попыткой** (условие единственного status=Forming недостаточно — проверяется generation).

S3-защита: ключ попытки включает exportId и generation (`exports/{exportId}/{generation}/data.csv`); загрузка не перезаписывает существующие байты по ключу (put-if-absent/уникальный ключ попытки); Ready сохраняет точный ключ, версию объекта, размер и checksum проверенных байтов; CAS-переход в Ready проверяет generation и допустимое состояние; старый worker не может ни заменить Ready, ни затереть объект новой попытки, ни удалить объект победителя; crash между upload и Ready восстанавливается из сохранённого состояния (та же попытка, тот же снимок) без изменения данных. Checksum в БД сам по себе не защищает от перезаписи общего ключа — поэтому ключ уникален для попытки.

Удаление: атомарно устанавливает Deleted и фиксирует долговременный `cleanup_intent`; немедленно запрещает выдачу новых ссылок — включая replay старого Idempotency-Key ссылки (409 после проверки доступа); исключает последующий Ready (CAS по generation и статусу). Очистка удаляет объекты **всех** попыток и незавершённые multipart-загрузки; одноразовой задачи недостаточно — cleanup-процесс по сохранённому intent периодически сверяет известные префиксы `exports/{exportId}/**` и удаляет поздние загрузки старых worker'ов; объект действующего Ready не затрагивается; срок ≤ 10 минут после восстановления зависимостей (T07).

### 3.7 Кеш (без изменений G2, R2-09 подтверждает)

Valkey: каталог (30 с) и live-рейтинг (5 с), чистый TTL без инвалидации; запись несёт `{payload, asOfUtc, expiresAtUtc = asOfUtc + допуск}`; возраст считается от `asOfUtc`, позднее заполнение не продлевает жизнь, значение с истёкшим `expiresAtUtc` — miss и не пишется (A2-09); `asOfUtc` обязателен в кешируемых ответах; при отказе — ограниченное ожидание (~200 мс) и чтение из PG. Права, балансы, экономические решения, лимиты выполнения из кеша не берутся (T06). Чтение из PG даёт `asOfUtc` = момент чтения; Final-рейтинг неизменяем — `asOfUtc` = момент финализации, допуск не применяется.

### 3.8 Сводная таблица инвариантов

| Инвариант | Где | Сценарий | Проверка |
|---|---|---|---|
| Replay/номер | §3.0, §2.1 колонки essential/response | E1/E2-контрпример; рестарт; тот же номер с иными данными | HTTP-тест: идентичность тела ответа повтору; 409 без записи |
| Пакетная награда | §3.1, §3.5 | 8A+5B при B=1 → Declined, завершение/очки сохранены | Functional + unit |
| Последнее средство | §3.5 | бюджет 10, две параллельные по 10 → одна Posted, остаток 0 | Параллельный functional + нагрузочный сценарий |
| Отмена | §3.1 | два конкурирующих возврата; нехватка одного баланса | Параллельный functional; индекс в persistence-группе |
| Смена прав | §3.0 шаг 2, §3.3 | блокировка между отправкой и обработкой; отзыв grant; смена владельца | Functional + проверки 03_AUTH |
| Параллельные завершения | §3.1 (stream_points), §3.2 | два задания стрима одновременно, порог между суммами → достижение один раз, ни одно не потеряно | Параллельный functional; мутация «убрать блокировку stream_points» роняет тест |
| Границы челленджа | §3.2 | трассы (а)–(д) | Unit-календарь; functional с TimeProvider; e2e ≤ 5 с |
| Снимок/попытка/удаление экспорта | §3.6 | поздний commit; crash upload→Ready; гонка удаления и worker; поздний upload старой попытки | E2E chaos; тест фиксированного снимка; тест generation |
| Мин-кап/завершения | §3.5 | E03, EDGE-01 | Unit + functional |
| Достижения | §3.5, §3.1 | EDGE-02 | Functional |
| Изоляция | тенант во всех запросах | masterId=123 в двух компаниях | Contract-тесты |

---

## 4. API

### 4.1 Соглашения

- Аутентификация: `Authorization: Bearer` JWT по 03_AUTH (HS256; iss/aud; `companyId/actorType/masterId/role`); дубликаты identity-claims отклоняются.
- Статусы: 201+Location (создание; экономический отказ — тоже 201 с `result: Declined`); 200 чтение; 204 удаление; ошибки — Problem Details (RFC 9457) с постоянным `code` и `traceId`; доступ/валидация — 4xx без ресурса.
- Повторы — единый порядок §3.0; replay возвращает исходные статус и представление. Конкурентный повтор — ограниченное ожидание или 503/Retry-After с обязанностью последующего повтора восстановить результат. Бизнес-номера бессрочны в PG; остальные создающие POST — `Idempotency-Key` ≥ 24 ч.
- If-Match — на всех изменениях настроек (PATCH/PUT/DELETE, включая отзыв grant); strong ETag; 428/412 (T04). Полный договор версий агрегатов — §4.4. Экономические операции и удаление выписки If-Match не используют (T04).
- Списки: `items + nextCursor`, limit 1..100 (по умолчанию 50), устойчивый порядок, keyset-курсор append-only истории не пропускает и не дублирует существовавшие записи. **Исключение — live-рейтинг: top N (limit) и отдельное собственное место, cursor-обход всей коллекции не обещается** (T04). Историю с cursor-гарантией дают списки операций/событий/аудита.
- Время/числа: Входные даты с UTC offset; выход UTC; целые с проверкой переполнения (превышение — 400 без частичных эффектов).

### 4.2 Коды ошибок

| HTTP | code | Случай |
|---|---|---|
| 400 | `validation.failed` / `validation.overflow` / `validation.code-format` / `validation.grant-target` | Поля, форматы, пределы чисел, неполное/недопустимое сочетание цели grant (A2-05) |
| 401 | `auth.invalid-token` | Подпись, iss/aud, срок, обязательные claims |
| 403 | `authz.forbidden` / `authz.grant-missing` / `authz.employee-not-active` | Роль/владение/grant; доступ к чтению по аудитории; действие от имени заблокированного профиля (включая повторы); активность получателя progress до номера (H0 Q04/Q08, §3.3) |
| 404 | `not-found` | Чужой или несуществующий объект компании |
| 409 | `conflict.business-number` / `conflict.idempotency-data` / `conflict.state` / `conflict.publish-check` | Номер с иными данными; неизменяемое состояние; провал проверки публикации |
| 412 / 428 | `precondition.failed` / `precondition.required` | If-Match |
| 424 | `dependency.unavailable` | Выдача ссылки при временном отказе S3 |
| 503 | `service.unavailable` | PostgreSQL недоступен (ограниченный по времени), Retry-After |

Экономические отказы (201): `InsufficientBudget`, `InsufficientFunds`, `InsufficientBalance`, `ResourceUnavailable`, `RecipientNotActive`, `RecipientNotInAudience`, `OriginalAlreadyReversed`, `OriginalNotPosted`. Отказы событий (201, `result: Rejected`): `AudienceMismatch`, `CampaignClosed`, `TargetArchived` — сохраняются и возвращаются повтором номера. H0 Q04/Q08: значение `EmployeeNotActive` остаётся в прежнем enum для совместимости сохранённых представлений, но ветка 201 Rejected/EmployeeNotActive недостижима для нового запроса: обязательная активность получателя проверяется до номера для нового обращения и replay, отказ — 403 без нового события. Ошибки валидации/прав и временный сбой без committed согласованного результата номер не занимают; committed результат, в том числе бизнес-отказ, занимает номер даже при потере ответа (§3.0).

### 4.3 Карта ресурсов

Изменения G2: `GET /me/progress-events` (новый, §3.4); рейтинг — доступ «после сезона», а не «после финала»; мутабельность — по матрице §4.5; PUT owner/resources — ETag кампании (§4.4). Полная карта семейств приведена ниже и в `openapi.yaml`.

| Ресурс | Методы | Права |
|---|---|---|
| /campaigns, /campaigns/{id}, /campaigns/{id}/owner, /campaigns/{id}/resources | GET, POST, PATCH, PUT, DELETE | создание — Admin; настройка — владелец/Admin; назначение владельца (PUT owner) — Admin; набор ресурсов (PUT) — владелец/Admin, только Draft; DELETE — только черновик |
| /campaigns/{id}/streams, /campaigns/{id}/challenges, /streams/{id}/tasks, /streams/{id}/milestones, /streams/{id}, /tasks/{id}, /milestones/{id}, /challenges/{id} | GET, POST, PATCH, DELETE | чтение — аудитория/владелец/Admin; настройка — владелец/Admin по матрице §4.5; DELETE — только черновик |
| /campaigns/{id}/progress | GET | владелец/Admin — прогресс участников |
| /progress-events | POST, GET | POST — service с grant Progress; GET — Admin |
| **/me/progress-events** | GET | сотрудник — собственная история (§3.4) |
| /challenges/{id}/leaderboard, …/me | GET | текущая аудитория — до конца сезона; после сезона — участники (по сохранённым данным), владелец, Admin; Final — состояние результата, не основание менять права досрочно |
| /employees, /employees/{masterId} | GET, POST, PATCH | Admin (GET своего профиля — сотрудник) |
| /resources, /resources/{id} | GET, POST, PATCH | GET — все участники компании; запись — Admin |
| /achievements, /achievements/{id} | GET, POST, PATCH | GET — все; запись — Admin |
| /campaigns/{id}/budgets, /campaigns/{id}/budget-allocations | GET, POST | GET — владелец/Admin; выделение — Admin; ресурс обязан входить в набор кампании |
| /integration-grants, /integration-grants/{id} | GET, POST, DELETE | Admin; цели задаются строгими сочетаниями (A2-05): Progress → campaign; Award → campaign + resource; Spend → resource + purchase system |
| /purchase-systems, /purchase-systems/{id} | GET, POST, PATCH | Admin |
| /spends | POST | Сотрудник (свой кошелёк) или система покупок с grant Spend (ресурс + система) |
| /campaigns/{id}/manual-awards | POST | Владелец/Admin; интеграция — grant Award (кампания + ресурс) |
| /reversals | POST | Admin; текущий владелец кампании-источника (начисления); система покупок — свои траты при действующем grant Spend (та же пара ресурс+система) |
| /me/wallet, /me/achievements, /me/campaigns/{id}/progress | GET | Сотрудник — свои |
| /me/operations | GET | Сотрудник — операции своего кошелька; service — траты/возвраты по действующим парам grant Spend своей компании, независимо от инициатора (§3.3) |
| /employees/{masterId}/wallet, /employees/{masterId}/operations | GET | Admin |
| /operations/{id} | GET | Инициатор, задействованный сотрудник, Admin; service — только история разрешённых пар своей системы покупок (§3.3) |
| /campaigns/{id}/operations | GET | Владелец (расходы кампании), Admin |
| /audit-records | GET | Admin |
| /exports, /exports/{id}, /exports/{id}/download-links | POST, GET, DELETE | Сотрудник — свои; Admin — сотрудник/компании своей фирмы; DELETE — заказчик |

Создания без бизнес-номера требуют `Idempotency-Key`; progress-events, spends, manual-awards, reversals, budget-allocations защищены собственными номерами источников.

### 4.4 Договор ETag версий агрегатов (R2-07)

Выбрана **версия агрегата кампании**: `campaigns.version` (int, strong ETag `"vN"`).

| Вопрос | Ответ |
|---|---|
| Источник ETag для PUT /campaigns/{id}/owner и PUT /campaigns/{id}/resources | `GET /campaigns/{id}` (доступен каждому, кому разрешено изменение: владельцу и Admin) |
| Что сравнивает сервер | If-Match с текущим campaigns.version |
| Где хранится | campaigns.version, инкремент в той же транзакции |
| Что делает ETag устаревшим | любое изменение агрегата: PATCH кампании, PUT owner, PUT resources, PUT/DELETE кампании, изменение/создание/удаление стрима, задания, вехи, челленджа (дочерние мутации повышают version, их собственные PATCH/DELETE дополнительно защищены их ETag из GET конкретного объекта) |
| Новый ETag | возвращается в ответе каждого успешного изменения (заголовок ETag; PUT owner — также обновлённое представление Campaign) |
| Атомарность | проверка If-Match и запись под `FOR UPDATE` строки campaigns в одной транзакции (§3.1, путь «публикация/настройки») |

Остальные изменяемые настройки (employees, resources, achievements, purchase-systems, integration-grants, milestones, streams, tasks) имеют полный цикл GET по id → If-Match → новый ETag в ответе мутации.

### 4.5 Матрица мутабельности (R2-09)

| Объект | Черновик | После публикации | После архива |
|---|---|---|---|
| Кампания | всё, кроме season/сроков после публикации (см. ниже); удаление целиком (§2.3) | name, description, audience, владелец (только Admin), архивация; сезон, сроки, состав заданий/ресурсов, цели, периодичность, награды, очки, правила рейтинга — неизменяемы (B09.5) | ничего |
| Стрим | всё; удаление с черновиком | name, архивация | ничего |
| Задание | всё; удаление с черновиком | **name, description, audience** (B09 не запрещает), архивация; код, goal, периодичность, очки, rewardItems — неизменяемы (B09.5: экономические параметры) | ничего |

Синхронизирована с §4.3 и PATCH-описаниями OpenAPI.

---

## 5. Короткие решения

**Аутентификация/авторизация.** JwtBearer HS256, ключ — декодированные Base64-байты, явный маппинг claims. Роль Admin — из токена (T03); владение, grants, активность, аудитория — данные приложения, проверяются в транзакции операции (не из кеша), в порядке §3.0 (для replay принятого события аудитория не пересчитывается; H0 Q04). Назначение владельца — отдельное Admin-полномочие (PUT /campaigns/{id}/owner); владелец настраивает свою кампанию без права переназначить себя (A2-04). Service-токен не имеет кошелька; masterId из запроса не подменяет субъект. Bootstrap — одна доверенная команда создания компании и активного администратора через use case.

**Кеш.** §3.7.

**Фоновые операции.** Outbox в PostgreSQL (`FOR UPDATE SKIP LOCKED`, lease с продлением, два worker'а): FinalizeChallenge (§3.2), ExportFormation (протокол §3.6), Cleanup (ссылки, объекты и незавершённые загрузки удалённых выписок ≤ 10 мин, idempotency ≥ 24 ч; повторная сверка префиксов по §3.6). Задача ставится в транзакции бизнес-изменения.

**Отказы.** PostgreSQL недоступен → ограниченный 503 + Retry-After, readiness «не готов», liveness не падает; Valkey → деградация до PG; S3 → основной учёт продолжается, экспорты повторяются; логи/метрики OpenTelemetry, correlation/traceId в ProblemDetails; токены, ключи, signed URL не логируются.

**Нагрузка.** 85% чтений (каталог/кошелёк/рейтинг) — Valkey + точечные индексированные чтения (балансы — из PG, §3.7); 15% записей — короткие транзакции на горячих строках; пулы Npgsql ограничены под 1 GiB; UUID v7 для локальности; целевые p95 ≤ 200/500 мс; проверяются итоговые бизнес-состояния. Условия эксперимента, не SLA.

**Слои тестов** (T08): Unit — правила Domain; Functional — реальные PG/Valkey/S3 через use cases и HTTP, включая параллельные сценарии §3; HTTP/contract — реальные JWT, статусы, ETag/If-Match, ProblemDetails, пагинация, повторы; Persistence/migrations — маппинги, индексы, CHECK, пустая БД и пошаговость; Architecture — анализаторы + направленные зависимости (L01); E2E — процессы, конкуренция, рестарты, отказы Valkey/S3/API/PG, неизменяемость снимка. Ожидания — из B-правил (SCN/EDGE).

Runtime/SLO-проверки на этой стадии не выполнялись — они спроектированы; дополнительные трассы — §3.2, параллельные проверки — §3.1/§3.8.

### 5.1 Ключевые спорные решения (вариант → причина → цена)

| Решение | Причина | Цена/ограничение |
|---|---|---|
| Пессимистичные блокировки бюджета/баланса вместо serializable | Детерминированность на горячих строках, PG-only | Очередь конкурентов в один бюджет; для 15 RPS записей приемлемо |
| Единая книга operations | B28.2, один CSV-путь | Широкая схема с явными nullable-полями |
| Outbox в PostgreSQL, не брокер | T01 не даёт брокера; атомарность с commit | Поллинг 1 с; целевой SLO 5 с (§3.2) |
| Материализованный снимок выписки | T07 запрещает замену фильтром по timestamp | До ~10⁵ строк на выписку, чистятся с ней |
| NodaTime в Domain | Корректная арифметика периодов/DST | Одна чистая зависимость |
| Блокируемый агрегат stream_points (A2-06) | Сериализация одновременных завершений одного стрима без потерянных/дублей достижений | Короткая очередь на (сотрудник, стрим, сезон) |
| Declined как 201-ресурс | T04 | Клиент обязан читать `result` |
| Advisory-lock финализации челленджа | Гонка «принятие в момент конца» | Замки всех челленджей стрима на событие (§3.2) |
| Возраст кеша от asOfUtc, «мёртворождённые» записи не пишутся (A2-09) | Допуск отставания выполняется безусловно | Редкая лишняя перестройка при задержанном заполнении |
| Кеш по TTL без инвалидации | Допуски покрывают устаревание | Окно ограничено допуском (§3.7) |

Решения R2: (1) `generation`-владение попыткой экспорта — цена: доп. колонка и строгие CAS-условия; (2) версия агрегата кампании как единый ETag подресурсов — цена: дочерние мутации повышают version; (3) канонический снимок ответа в самой операции — цена: хранение тела ответа, зато идентичность replay без пересборки.

---

## 6. Покрытие

### 6.1 B01–B37 (изменения R2)

| B | Механизм |
|---|---|
| B01 | /employees POST: профиль+кошелёк одной транзакцией; уникальность masterId |
| B02 | company_id из токена во всех store/запросах; чужое — 404 |
| B03 | Сотрудник: `GET /campaigns` → `GET /campaigns/{id}/streams` → `GET /streams/{id}/tasks` (доступные кампании/задания) + `/me/*`; владелец: `/campaigns/{id}/progress`, budgets, operations; владелец назначается только Admin (PUT owner) |
| B04 | grants со строгими сочетаниями; `/me/operations` и `/operations/{id}` для service — траты/возвраты своей компании по реально выданным парам purchaseSystemId/resourceId, независимо от инициатора; актуальные права на каждом чтении (§3.3) |
| B05 | Проверки в транзакции операции; повторы тоже (§3.0, §3.3) |
| B06 | /resources: нормализованный код, уникальность, неизменяемость, нулевые балансы |
| B07 | Архивация PATCH статуса; Declined `ResourceUnavailable`; отмена после архивирования |
| B08 | Уникальные индексы кодов; **campaign_resources управляется GET/PUT /campaigns/{id}/resources (владелец/Admin, до публикации)**; budgets на пару |
| B09 | PATCH status → Published (проверка состава, 409 publish-check), неизменяемые поля (409), DELETE черновика |
| B10 | Календарь Domain; кампания в сезоне |
| B11 | Теговые наборы кампании И задания (task_tags + campaign_tags), проверка совместно + сроки; /me/campaigns/{id}/progress |
| B12 | Валидация заданий и reward items (ресурс из набора кампании) |
| B13 | Календарь периодов (NodaTime), TimeProvider |
| B14 | acceptedAt по протоколу §3.2 (время после замков, TimeProvider) |
| B15 | unique завершений; события после цели — credited 0 |
| B16/B24 | бессрочные номера + канонические данные/исходный ответ в operations/progress_events (§3.0); 409 при расхождении существенных данных |
| B17/B20/B25 | §3.1: отказ фиксируется в транзакции, откат не уничтожает Declined |
| B18 | /budget-allocations (Admin, ресурс из набора кампании), история = операции |
| B19 | Счётчики budgets + CHECK; атомарность бюджет↔баланс |
| B21 | /manual-awards (владелец/Admin/grant Award); не меняет прогресс |
| B22 | /me/wallet; один баланс на ресурс; CHECK ≥ 0 |
| B23 | /spends; активность владельца кошелька; отказы 201 Declined |
| B26 | /reversals для начислений (балансы пакета или Declined) |
| B27 | /reversals для трат (Admin/система покупок при действующем grant Spend) |
| B28 | Схема Operation; /audit-records |
| B29/B30 | stream_points — общая точка координации; вехи проверяются совместно с обновлением очков |
| B31 | Челлендж до публикации; счёт из зачтённых дельт |
| B32 | доступ «после сезона» (не «после финала»); собственное место при отсутствии участия — `participating: false`, score/place = null (без выдуманного нулевого места) |
| B33 | FinalizeChallenge job, immutable challenge_results |
| B34 | + `GET /me/progress-events` — история выполнения/наград с причинами отказов |
| B35 | CSV: BudgetAllocation — не движение кошелька, в CSV не входит (включая экспорт по компании) |
| B36/B37 | §3.6: generation-попытки, неизменяемые байты, запрет воскрешения, очистка по intent |

### 6.2 T01–T10

| T | Как |
|---|---|
| T01 | Стек зафиксирован; версии SDK/образов — перед этапом 3; миграции только PG 17 |
| T02 | Проекты/зависимости §1 + анализаторы и арх-тесты (L01) |
| T03 | JWT §5, bootstrap-команда, без login endpoints, активность профиля в транзакции |
| T04 | REST §4; If-Match на всех изменениях настроек + ETag-источники (GET по id для grants/milestones/challenges); единый envelope списков с исключением live-рейтинга (§4.1); таблицы покрытия 6.1 |
| T05 | Порядок «авторизация → сохранённый результат → предусловия» §3.0; TimeProvider |
| T06 | PG-инварианты §3; правило возраста кеша §3.7; 2 API + 2 worker |
| T07 | Протокол экспорта §3.6 (заморозка, идемпотентные шаги, Ready, ссылки, очистка) |
| T08 | Слои §5, включая параллельные тесты §3 |
| T09 | Подход §5; «два конкурента» §3 |
| T10 | Compose/README/примеры — этап 3 |

T04/T05/T06/T07 дополнены §3.0, §3.1, §3.6, §4.4.

---

## 7. Согласования и валидация

### 7.1 Валидация OpenAPI (реально выполнено)

При внешней нормализации выполнена собственная проверка через `openapi-spec-validator` (`OpenAPIV30SpecValidator`) и `openapi-schema-validator` (`OAS30Validator`), без опоры на прежнюю запись автора. Команда: `/private/tmp/motiva-stage2-validation/bin/python /private/tmp/motiva-validate.py` (временный проверочный скрипт, целевой файл — `docs/openapi.yaml`); exit code 0. OpenAPI 3.0.3 / version 1.2.0: PASS; 521 локальный `$ref` разрешён; 44 маршрута / 70 методов, форма DTO/required/enum/nullable сохранена. Позитивные/негативные полные DTO для completion/refusal/reject: 7/13 — PASS; дополнительно проверены совпадение nullable-копии completion и 203 граничных payload H0 Q06/Q07.

Ограниченная ручная сверка: покупка сотрудника и возврат Admin видны системе по действующей паре grant; чужие компания/система и невыданная пара не видны; отзыв действует для списка, cursor и detail; сотрудник не получает чужую историю. Права/активность progress проверяются до номера; экономический отказ сохраняется, replay после commit возвращает исходный status/body. Пять прежних трасс (последнее средство, повтор после commit, отмена пакета, отзыв grant перед replay, crash после S3 upload) сохранены; новые алгоритмы/тестовые задачи не добавлены. Backend, миграции, нагрузка и runtime-проверки не запускались; независимый gate этим не подтверждён.

### 7.2 Проверки payload (обязательный набор)

Позитивные: событие с `completion: null`; событие с корректным completion; отказ награды с ненулевым `refusalCode`; Posted-операция с `refusalCode: null`; Accepted-событие с `rejectReason: null`; ненулевой допустимый `rejectReason`; пустая страница истории (`items: []`). Негативные: неизвестный код enum; completion неверного типа; completion без обязательных полей. Проверяются целые DTO.

### 7.3 Противоречия входов

Прямых противоречий входов не выявлено. Сопоставления: С1 — CSV «номер операции» = `operationId` (внешний номер — в API, header CSV фиксирован T07); С2 — «первые N» = `limit` списков (AMB-05); С3 — T07 закрывает AMB-07 (автоматическая финализация в момент конца); С4 — T07 закрывает AMB-13 (ссылка ≤ 60 с).

---

### 7.4 Нормативные уточнения организатора H0

H0 Q01–Q09 внесены при внешней нормализации. Это общие уточнения постановки, а не новые архитектурные решения и не утверждение, что автор получал их ранее.

| H0 | Норма |
|---|---|
| Q01 | Ручное начисление допустимо активному сотруднику текущей аудитории действующей опубликованной кампании; предыдущее выполнение/прогресс не требуется (B21, AMB-04). |
| Q02 | Для непустого пакета недоступность любого входящего ресурса приоритетнее нехватки бюджета: `ResourceUnavailable` перед `InsufficientBudget`. Посторонний архивный ресурс не влияет; пустой пакет — награда не предусмотрена (B07/B20). |
| Q03 | При заблокированном получателе корректирующую отмену выполняет только Admin; владелец без Admin и сервис не могут, проверка действует перед replay (B05/B26/B27, AMB-02/03). |
| Q04 | Replay принятого события при актуальных правах источника и активности сотрудника не пересчитывает аудиторию. До конца сезона рейтинг читает текущая аудитория, в том числе после конца/архива кампании; потерявший аудиторию не читает. После сезона сотруднику нужна собственная историческая строка именно этого челленджа, включая счёт 0; права владельца/Admin сохраняются (B16/B32). |
| Q05 | Сроки кампании и диапазон выписки — `[начало, конец)`, начало строго раньше конца. Исключённый конец кампании может совпадать с началом следующего сезона. Укороченный крайний период не уменьшает цель/награду; отбор выписки по времени движения не заменяет фиксированный снимок B36 (B10/B13/B35, T07). |
| Q06 | goal/delta/порог — целые > 0; очки/прогресс/зачтённая дельта/счёт — целые ≥ 0. Дроби отклоняются без округления; нулевые очки и пустая награда допустимы. Верхние пределы сохраняются по T05. |
| Q07 | masterId — число 1…2 147 483 647; сравнение/сортировка числовые, ведущие нули не создают другую личность. У кодов удаляются обычные крайние пробелы; сравнение без регистра, пустое запрещено, внутренние символы значимы и действует алфавит T05. Внешние номера сравниваются точно, без такой нормализации (B01/B06/B08/B32, T05). |
| Q08 | Ошибки валидации/прав не занимают номер; сохранённый бизнес-отказ занимает и воспроизводится. Временный технический сбой без committed согласованного результата номер не закрепляет; commit с потерей ответа номер не освобождает (B16/B17/B24, T05). |
| Q09 | Выделения допустимы в Draft/Published до конца срока/сезона кампании, включая будущую кампанию и время до её начала. Архивной, завершившейся или прошлосезонной кампании новые выделения запрещены. Допустимая отмена возвращает средства в исходный старый бюджет. Удаление черновика сохраняет выделения, номера и финансовую историю без автоматического возврата/переноса (B09/B10/B18/B26). |

## 8. Открытые вопросы и решения

Соответствие прежних AMB из неизменённого `stage-1.md` окончательным правилам общего входа: H0 (§7.4) закрывает указанные бизнес-уточнения; остальные решения перенесены из текущего авторского §8. Старые открытые статусы stage-1 не означают повторного ожидания решения по этим пунктам.

| ID | Статус | Основание |
|---|---|---|
| AMB-02 (отмена владельцем начисления заблокированному) | закрыт H0 Q03: только Admin; владелец без Admin — 403, включая replay | B05/B26 |
| AMB-03 (возврат Service заблокированному) | закрыт H0 Q03: только Admin; Service — 403, включая replay | B05/B27 |
| AMB-04 («участник» для ручного начисления) | закрыт H0 Q01: активный сотрудник текущей аудитории действующей опубликованной кампании, прежний прогресс не требуется | B21 |
| AMB-14 (количество челленджей) | B31 не задаёт верхнего лимита; контракт уже поддерживает коллекцию, дополнительное ограничение не вводится; вопрос не препятствует планированию текущего объёма | B31.1 |
| AMB-05 (N) | разрешён: N = limit списков T04 (50/100) | T04 |
| AMB-06 (окно до конца сезона) | разрешён, уточнён H0 Q04: текущая аудитория читает и после конца/архива кампании; после сезона — собственная историческая строка именно этого челленджа, включая счёт 0; права владельца/Admin сохранены | B32.5 |
| AMB-07 (триггер финализации) | разрешён: автоматическая финализация в момент конца интервала | T07 (≤ 5 с) |
| AMB-08 (номер при отказе события) | разрешён H0 Q08: сохранённый бизнес-отказ занимает номер; валидация/права и временный сбой без commit — нет. Проверки активности progress до номера для нового обращения/replay (§3.0/§3.3); потеря ответа после commit номер не освобождает | B16/B17, T05 |
| AMB-09 (replay при потере аудитории) | разрешён, подтверждён H0 Q04: аудитория не пересчитывается; актуальные права источника и активность проверяются до номера | B16.4 |
| AMB-11 (момент фиксации снимка) | разрешён нормой + решение этапа: «первое успешное начало» = атомарный переход Pending→Forming с материализацией состава | B36.2, §3.6 |
| AMB-12 (состав проверки публикации) | решение этапа (конкретизация B09.4): поля заданий, награды из набора кампании, вехи/достижения компании, челленджи в сроках и по одному стриму, аудитории, сезон | B09.4 |
| AMB-13 (срок ссылки) | разрешён: 60 секунд | T07 |

Открытые технические пункты этапа 3: выбор S3-образа и фиксация версий SDK/пакетов (T01).

Внешняя нормализация ограничена этими двумя документами. Авторский G2 остаётся непройденным; следующее действие организатора — независимая проверка нормализованного входа, затем стадия 3 с пометкой `EXTERNALLY_NORMALIZED`.
