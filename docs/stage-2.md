# Motiva — Stage 2. Архитектура и API (ревизия после R2)

Статус: исправленная версия, ожидает внешней проверки. Реализация, миграции и runtime-проверки не выполняются — проектируются; реально выполнена только валидация OpenAPI (§7.1).
Входы: `docs/01_BUSINESS.md` v3.0 (B01–B37, E01–E12), `docs/02_ENGINEERING.md` v3.0 (T01–T10), `docs/03_AUTH.md` + `auth/jwt.py`, `.editorconfig`, утверждённый `docs/stage-1.md`. Предыдущие сдачи сохранены в Git (a847d7d — H0, 2a345c4 — G2).
Контракт: `docs/openapi.yaml` (OpenAPI 3.0.3, валидация — §7.1).

---

## 1. Границы модулей и зависимости

Без изменений относительно G2: проекты Motiva.Domain / Application / Infrastructure / Api / Worker; Domain зависит только от BCL и NodaTime (явно разрешённая чистая зависимость — календарная арифметика периодов/сезонов/DST); Application — только Domain; Infrastructure реализует порты Application (EF Core 10 + Npgsql/PostgreSQL 17, Valkey 9, S3, outbox); composition root — только `Program.cs` Api и Worker. Порты и их обоснование — G2 §1.3 (без изменений). Архитектурные проверки: BannedApiAnalyzers запрещают инфраструктурные API вне Infrastructure/composition root на компиляции; тесты направленных зависимостей проверяют рёбра проектов и namespaces, подсаженное нарушение обязано ронять тест. Группировка по функциям: Progress, Rewards, Wallets, Budgets, Campaigns, Competitions, Exports, Administration, Audit.

---

## 2. Модель данных

Соглашения: PostgreSQL 17; `company_id` на каждой строке (тенант из проверенного токена); UUID v7; `timestamptz` UTC; суммы `bigint` (баланс/бюджет ≤ 10¹⁵, цели/дельты/очки ≤ 10⁹); коды нормализуются в верхний регистр; внешние номера 1–100 ASCII регистрозависимы. EF-сущности ≠ DTO. Уровень изоляции всех бизнес-транзакций — READ COMMITTED (единственное намеренное исключение — материализация снимка экспорта, §3.8).

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
| **export_requests** | company_id, id, requested_by, scope, target_master_id, resource_id, from_utc, to_utc, status (Pending/Forming/Ready/Error/Deleted), **generation int ≥ 0**, frozen_at, snapshot_saved bool, ready_at, **s3_key, s3_version**, size_bytes, checksum, error_detail, created_at, lease_owner, lease_until, **cleanup_intent bool** | один заказ — много попыток; generation — владелец попытки (§3.8) |
| export_operation_ids | export_id, operation_id | PK пара — материализованный снимок B36 |
| download_links | id, export_id, expires_at, created_at, idem_key | expires ≤ created + 60 с |
| idempotency_keys | company_id, initiator_key, operation, target_id, key, response_status, response_body, created_at | PK составной; TTL ≥ 24 ч — только для операций без бизнес-номера |
| outbox_jobs | id, type, payload, available_at, attempts, status, locked_by, locked_until | `FOR UPDATE SKIP LOCKED` + lease |
| audit_records | company_id, id, actor…, action, entity_type, entity_id, changes, created_at | append-only |

### 2.2 Бюджеты, кошелёк, движения

- Бюджет — строка на пару «кампания × ресурс»; ресурс обязан входить в `campaign_resources` (API до публикации, G2). `allocated_total` растёт только BudgetAllocation (Admin). Инвариант `allocated − spent + returned ≥ 0` — `FOR UPDATE` + атомарные счётчики + CHECK. Траты и возвраты трат бюджет не меняют (B23.6, B27.3).
- Кошелёк 1:1 с профилем; один баланс на ресурс независимо от кампании-источника; CHECK `balance ≥ 0`.
- **Движение личного кошелька — Posted operation_item только видов TaskReward / ManualAward / Spend / SpendReversal / AwardReversal** (кредит/дебет по виду). **BudgetAllocation хранится в финансовой истории, но личный кошелёк не меняет и в CSV не попадает, включая экспорт по компании** (B28.2, T07). Отклонённые операции хранят попытленные позиции, в балансы и CSV не входят. CSV-контракт T07 неизменен: `operationId,masterId,resourceCode,amount,kind,campaignId,originalOperationId,createdAtUtc`, сортировка createdAtUtc, operationId, resourceCode, диапазон [from,to).

### 2.3 Удаление и история

| Данные | Правило |
|---|---|
| Опубликованные кампании, ресурсы, достижения, сотрудники | Физического удаления нет: архив/блокировка |
| **Черновик кампании с финансовой историей** | DELETE переводит campaigns в **Deleted-надгробие**: настройки (стримы, задания, вехи, челленджи, теги, набор ресурсов) удаляются каскадом; код освобождается (code_norm перезаписывается служебным значением с id); **operations / operation_items / номера / audit сохраняются со ссылкой на надгробие** — финансовая история не каскадится и остаётся объяснимой (B25.1, B28). Бюджетирование черновика не запрещается — история сохраняется, а не предотвращается |
| operations, operation_items, progress_events, completions, stream_points, achievement_grants, challenge_results, audit_records | Append-only / монотонные счётчики; коррекция — операцией отмены |
| export_requests | Мягкое Deleted + cleanup_intent; жизненный цикл — §3.8. **Истечение ссылки или lease саму выписку не удаляет** — такого бизнес-правила нет |
| download_links, idempotency_keys | Истечение срока / TTL ≥ 24 ч |

---

## 3. Инварианты и протоколы

### 3.0 Единый порядок обработки любой операции (R2-01)

1. **Проверенная идентичность и компания**: подпись/iss/aud/claims JWT; `companyId` из токена; сотрудник определяется токеном, masterId из запроса его не переопределяет.
2. **Актуальные полномочия и обязательные проверки активности**: роль, действующий grant, текущее владение, активность инициатора (user) и обязательная активность получателя там, где норма требует её и для повтора (B16.4). Нарушение → 401/403 **без записи и без эффекта**; ошибка доступа никогда не превращается в сохранённый успех/Declined/Rejected.
3. **Бизнес-номер**: уникальность события — (company, проверенный source subject, eventNumber); экономической операции — (company, проверенный initiator, kind, operationNumber); **кампания в ключ не входит** — campaignId (в т.ч. из URL) входит в существенные данные ручного начисления и выделения. Найден номер + совпадают существенные данные → **возврат сохранённого исходного ответа** (`response_status` + `response_body` из §2.1; исходные ID, статус, представление; добайтовая сборка не выполняется); совпадают не все → 409 `conflict.business-number` без нового эффекта. Неопределённость «закоммитился ли прошлый запрос» разрешается повторным поиском сохранённого результата, а не повторным выполнением эффекта.
4. **Только для нового действия** — бизнес-предусловия (аудитория, архив, сроки, бюджет, баланс) и сам эффект.

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
| Событие с завершением/наградой | одна READ COMMITTED; advisory челленджей → progress_state → stream_points → budgets → wallet_balances | §3.2, затем 4–7 | ожидание блокировки; логический исход (Accepted/Rejected, награда Posted/Declined) не зависит от порядка конкурентов |
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

Трассы проверки: (а) `accepted_at < endsAt`, commit/ответ задержаны — событие в итоге; (б) запрос пришёл до `endsAt`, но замок получен после — в завершившийся челлендж не входит; (в) `accepted_at = startsAt` входит, `accepted_at = endsAt` не входит; (г) несколько пересекающихся челленджей — каждый получает свою дельту, замки в одном порядке, взаимоблокировки нет; (д) повтор события и отмена награды итог не меняют. REPEATABLE READ остаётся только для материализации снимка экспорта (§3.8).

### 3.3 Матрица прав по состояниям участников (R2-04)

| Ситуация | Исход | Обоснование |
|---|---|---|
| user-инициатор неактивен (любое действие, включая replay) | 403 `authz.employee-not-active`, без записи | B05.1–B05.2 |
| Service: проверенный subject → действующий grant → конкретные purchaseSystemId и resourceId; исходная трата принадлежит разрешённой системе. Subject ≠ purchaseSystemId; владение одной системой не даёт весь кошелёк | иначе 403 без записи (включая replay) | B04, T03 |
| Grant отозван (новое действие и replay) | 403 без записи; прежние результаты не аннулируются | B05.1, B16.4 |
| Бывший владелец после смены | 403; полномочия у текущего владельца | B05.1 |
| Инициатор активен/полномочен, получатель заблокирован, **новая** операция (событие, начисление, трата) | сохраняемый отказ: событие — 201 `Rejected` (`EmployeeNotActive`); операция — 201 `Declined` (`RecipientNotActive`); Service ограничения не обходит | B05.2 |
| **Отмена начисления / возврат траты** (коррекция) при заблокированном получателе | Admin — разрешено (B05.2, B26.6, B27); владельцу и Service — открытые AMB-02/03, до решения заказчика консервативный 403; активности получателя коррекция не требует | B26–B27 |
| Admin выполняет коррекцию заблокированного кошелька | разрешено; не превращается в разрешение нового начисления/списания | B05.2 |

Общее правило получателя: активность требуется для **новых** прогресса/начислений/трат на его кошельке; для коррекций общего требования активности нет (иначе была бы заблокирована разрешённая Admin-коррекция).

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

Возраст значения считается от `asOfUtc` снимка данных; TTL = asOfUtc + допуск (30 с каталог / 5 с live-рейтинг); «мёртворождённые» записи не пишутся и не обслуживаются; `asOfUtc` обязателен в кешируемых ответах; при отказе Valkey — ограниченное ожидание и чтение из PostgreSQL; права, балансы, экономические решения из кеша не берутся.

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

- Статусы/ProblemDetails/коды — G2 без изменений (экономический отказ — 201 Declined; доступ/валидация — 4xx без ресурса).
- Повторы — единый порядок §3.0; replay возвращает исходные статус и представление.
- If-Match — на всех изменениях настроек; strong ETag; 428/412 (T04). Полный договор версий агрегатов — §4.4.
- Списки: `items + nextCursor`, limit 1..100 (по умолчанию 50), устойчивый порядок, keyset-курсор append-only истории не пропускает и не дублирует существовавшие записи. **Исключение — live-рейтинг: top N (limit) и отдельное собственное место, cursor-обход всей коллекции не обещается** (T04). Историю с cursor-гарантией дают списки операций/событий/аудита.
- Время/числа — G2 (UTC offset на входе, UTC на выходе, точная целочисленная арифметика, переполнение — 400).

### 4.2 Коды ошибок

Без изменений относительно G2: `validation.*` (включая `validation.grant-target`), `auth.invalid-token`, `authz.forbidden` / `authz.grant-missing` / `authz.employee-not-active`, `not-found`, `conflict.business-number` / `conflict.idempotency-data` / `conflict.state` / `conflict.publish-check`, `precondition.required` / `precondition.failed`, `dependency.unavailable` (424), `service.unavailable` (503). Экономические отказы (201): `InsufficientBudget`, `InsufficientFunds`, `InsufficientBalance`, `ResourceUnavailable`, `RecipientNotActive`, `RecipientNotInAudience`, `OriginalAlreadyReversed`, `OriginalNotPosted`. Отказы событий (201, `result: Rejected`): `EmployeeNotActive`, `AudienceMismatch`, `CampaignClosed`, `TargetArchived` — сохраняются и возвращаются повтором номера (B16.2: отказ — тоже результат; B16.5 освобождает номер только для неверных данных).

### 4.3 Карта ресурсов

Изменения G2: `GET /me/progress-events` (новый, §3.4); рейтинг — доступ «после сезона», а не «после финала»; мутабельность — по матрице §4.5; PUT owner/resources — ETag кампании (§4.4). Остальное (employees, resources, achievements, campaigns, streams/tasks/milestones/challenges, budgets/allocations, grants, purchase-systems, progress-events, spends/manual-awards/reversals, wallets/operations, exports, audit) — как в G2 и в `openapi.yaml`.

| Ресурс | Методы | Права |
|---|---|---|
| /campaigns, /campaigns/{id}, /campaigns/{id}/owner, /campaigns/{id}/resources | GET, POST, PATCH, PUT, DELETE | создание — Admin; настройка — владелец/Admin; назначение владельца (PUT owner) — Admin; набор ресурсов (PUT) — владелец/Admin, только Draft; DELETE — только черновик |
| /campaigns/{id}/streams, /campaigns/{id}/challenges, /streams/{id}/tasks, /streams/{id}/milestones, /streams/{id}, /tasks/{id}, /milestones/{id}, /challenges/{id} | GET, POST, PATCH, DELETE | чтение — аудитория/владелец/Admin; настройка — владелец/Admin по матрице §4.5; DELETE — только черновик |
| /campaigns/{id}/progress | GET | владелец/Admin — прогресс участников |
| /progress-events | POST, GET | POST — service с grant Progress; GET — Admin |
| **/me/progress-events** | GET | сотрудник — собственная история (§3.4) |
| /challenges/{id}/leaderboard, …/me | GET | текущая аудитория — до конца сезона; после сезона — участники (по сохранённым данным), владелец, Admin; Final — состояние результата, не основание менять права досрочно |
| Прочие семейства | — | как в G2 |

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

Аутентификация/авторизация — G2 + §3.3. Кеш — §3.7. Фоновые операции — G2 (outbox: FinalizeChallenge, ExportFormation по §3.6, Cleanup с повторной сверкой префиксов). Отказы — G2 (503/Retry-Atter при недоступности PostgreSQL; деградация кеша; S3 не останавливает учёт). Нагрузка — G2. Тесты — G2 + трассы §3.2, параллельные тесты §3.1/§3.8; runtime/SLO-проверки на этой стадии не выполнялись — они спроектированы.

Спорные решения G2 сохранены; новые: (1) `generation`-владение попыткой экспорта — цена: доп. колонка и строгие CAS-условия; (2) версия агрегата кампании как единый ETag подресурсов — цена: дочерние мутации повышают version; (3) канонический снимок ответа в самой операции — цена: хранение тела ответа, зато идентичность replay без пересборки.

---

## 6. Покрытие

### 6.1 B01–B37 (изменения R2)

| B | Механизм |
|---|---|
| B03 | Сотрудник: `GET /campaigns` → `GET /campaigns/{id}/streams` → `GET /streams/{id}/tasks` (доступные кампании/задания) + `/me/*`; владелец: `/campaigns/{id}/progress`, budgets, operations; владелец назначается только Admin (PUT owner) |
| B04 | grants со строгими сочетаниями; `/me/operations` для service — цепочка subject → grant → purchaseSystemId/resourceId → своя история |
| B14 | acceptedAt по протоколу §3.2 (время после замков, TimeProvider) |
| B16/B24 | бессрочные номера + канонические данные/исходный ответ в operations/progress_events (§3.0); 409 при расхождении существенных данных |
| B17/B20/B25 | §3.1: отказ фиксируется в транзакции, откат не уничтожает Declined |
| B29/B30 | stream_points — общая точка координации; вехи проверяются совместно с обновлением очков |
| B32 | доступ «после сезона» (не «после финала»); собственное место при отсутствии участия — `participating: false`, score/place = null (без выдуманного нулевого места) |
| B34 | + `GET /me/progress-events` — история выполнения/наград с причинами отказов |
| B35 | CSV: BudgetAllocation — не движение кошелька, в CSV не входит (включая экспорт по компании) |
| B36/B37 | §3.6: generation-попытки, неизменяемые байты, запрет воскрешения, очистка по intent |

Остальные строки — G2 без изменений.

### 6.2 T01–T10 — G2 без изменений; T04/T05/T06/T07 дополнены §3.0, §3.1, §3.6, §4.4.

---

## 7. Согласования и валидация

### 7.1 Валидация OpenAPI (реально выполнено)

Инструменты: `openapi-spec-validator` (Python, проверка документа на соответствие OpenAPI 3.0.3) и `openapi-schema-validator` (`OAS30Validator`, проверка примеров payload по схемам 3.0). Команды и результаты — в сообщении сдачи; здесь зафиксировано, что позитивные/негативные проверки §7.2 выполнены фактическим запуском.

### 7.2 Проверки payload (обязательный набор)

Позитивные: событие с `completion: null`; событие с корректным completion; отказ награды с ненулевым `refusalCode`; Posted-операция с `refusalCode: null`; Accepted-событие с `rejectReason: null`; ненулевой допустимый `rejectReason`; пустая страница истории (`items: []`). Негативные: неизвестный код enum; completion неверного типа; completion без обязательных полей. Проверяются целые DTO.

### 7.3 Противоречия входов

Прямых противоречий нет; сопоставления С1–С4 G2 сохранены.

---

## 8. Открытые вопросы и решения

| ID | Статус | Основание |
|---|---|---|
| AMB-02 (отмена владельцем начисления заблокированному) | **открыт**: варианты — разрешить (отмена не «новое начисление») / запретить; до решения консервативный 403 (не бизнес-правило, только ветка авторизации) | B26.6 упоминает только административную коррекцию |
| AMB-03 (возврат Service заблокированному) | **открыт**: те же варианты; до решения консервативный 403 | B27 ↔ B05.2 |
| AMB-04 («участник» для ручного начисления) | **открыт**: любой активный аудитории / только с прогрессом; до решения — минимальное чтение B21.1+B11.4 (активный в аудитории), помечено как неутверждённое | B21.1 |
| AMB-14 (количество челленджей) | **открыт**: контракт — коллекция без ограничения; ограничение краткости не вводится | B31.1 |
| AMB-05 (N) | разрешён: N = limit списков T04 (50/100) | T04 |
| AMB-06 (окно до конца сезона) | разрешён: прямое чтение B32.5 — аудитория читает, «после сезона» переключает доступ | B32.5 |
| AMB-07 (триггер финализации) | разрешён: автоматическая финализация в момент конца интервала | T07 (≤ 5 с) |
| AMB-08 (номер при отказе события) | разрешён: отказ — результат, повтор возвращает его (B16.2); номер освобождается только для неверных данных (B16.5) | B16.2+B16.5 |
| AMB-09 (replay при потере аудитории) | разрешён: аудитория не входит в повторную проверку; активность и права источника проверяются | B16.4 |
| AMB-11 (момент фиксации снимка) | разрешён нормой + решение этапа: «первое успешное начало» = атомарный переход Pending→Forming с материализацией состава | B36.2, §3.6 |
| AMB-12 (состав проверки публикации) | решение этапа (конкретизация B09.4): поля заданий, награды из набора кампании, вехи/достижения компании, челленджи в сроках и по одному стриму, аудитории, сезон | B09.4 |
| AMB-13 (срок ссылки) | разрешён: 60 секунд | T07 |

Открытые технические пункты этапа 3: выбор S3-образа и фиксация версий SDK/пакетов (T01).

Стадия 2 (ревизия R2) завершена; к планированию и реализации не перехожу до внешнего решения.
