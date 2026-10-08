# Stage 4 — реализация Motiva

Статус: **R2** — второй раунд исправлений после внешней оценки R1/F (44,5/100, gate FAIL).
R0 сохранён в commit `c9912e2`, R1 — в `f91d953`; оба раздела ниже сохранены как история.
R2 закрывает остатки R1 (D01, D05–D09, D11, D13, D15, D16) и регрессию D18; найдены и
исправлены три дополнительных реальных дефекта (даты с «Z», восстановление form-export,
чистый старт jwt.py). Все команды ниже выполнены на финальном commit; артефакты — `artifacts/`.

## 0. Коммит R2

См. `git log -1`. База R2-кода — `2d21bb9` + данный документ.

## 0.1 Изменения R2 по каждому пункту обратной связи (D01–D18)

| Пункт | Статус R1 | Что изменено в R2 | Доказательство |
|---|---|---|---|
| D01 утечка detail заблокированным Admin | частично | Решение о видимости операции перенесено из Infrastructure в `ReadService.GetOperationAsync` (store — чистый `GetByIdAsync`); перед любым чтением — `CurrentRights.EnsureActiveEmployeeAsync` (активность из БД, не из JWT). Полный контроль видимости кампаний `CampaignsService.EnsureVisibleAsync` (detail + все content-чтения через него); активность в `CatalogService` (кампании, ресурсы, достижения) | Functional `d01_blocked_admin_cannot_read_foreign_operation_detail`, `d01_blocked_user_cannot_read_campaign_detail_or_catalog`; HTTP `blocked_admin_get_of_foreign_operation_is_403_over_http` (точная трасса проверки: награждение 5 → блокировка → GET 403, без masterId в теле) |
| D05 список выдаёт скрытое задание | частично | `ListVisibleTasksAsync`: аудитория задания фильтруется во всех списках; курсорная пагинация без потерь (граница после последнего возвращённого, не последнего прочитанного) | Functional `d05_task_list_excludes_out_of_audience_tasks_and_pages_losslessly` (постраничный обход size=1: скрытое не выдано ни на одной странице, видимое достижимо) |
| D06 архивный ресурс в новых настройках | частично | `ValidateRewardItemsAsync` требует статус Active у каждого ресурса награды — создание И изменение задания (purchase system и campaign set закрыты в R1) | Functional `d06_new_task_settings_reject_archived_resource` (ресурс в наборе draft-кампании → архив → create 400 и patch 400) |
| D07 If-Match дочерних сущностей | частично | `LockCampaignRowAsync` (FOR UPDATE + tracker reset) во ВСЕХ дочерних мутациях: PatchStream, PatchTask, DeleteMilestone, DeleteChallenge — версия агрегата меняется атомарно | Functional `d07_…` (8 параллельных PATCH задания: 1×200 + 7×412, версия v2 свежим scope); HTTP `concurrent_task_patch_…` (8 параллельных: 1×200 + 7×412, ETag `"v2"`) |
| D08 существенные данные campaign | частично | essential = {code, season, name, description, ownerMasterId, startsAt, endsAt, audience(канониз.)}; порядок §3.0: права → сохранённый replay → бизнес-предусловия (проверка владельца после echo) | Functional `d08_campaign_essentials_cover_description_and_window` (изменение description/startsAt → 409; идентичный replay → исходные 201/тело/Location), `d08_committed_campaign_replay_survives_a_later_owner_block` |
| D09 Location при replay сотрудника | частично | `IdempotencyGate.LocationOf` понимает `id` (строка/число) и `masterId` (число) | Functional `d09_employee_creation_replay_keeps_location`; HTTP `employee_creation_replay_keeps_location_over_http` (201 + тот же Location `/api/v1/employees/4242`) |
| D11 очистка при удалении+поздней загрузке+сбое | частично | (а) `HasLiveLeaseAsync` — lease жив независимо от статуса строки (Deleted с живым lease не даёт снять intent); (б) фенсинг каждой части: перед yield — строка не Deleted + продление lease (TimeProvider, не wall-clock); зомби-воркер прерывает multipart сам; (в) победившая попытка удаляет объекты проигравших (`DeleteOthersAsync`, включая незавершённые multipart); (г) form-export job при живом чужом lease НЕ завершается молча — RetryAt+15 c (раньше экспорт зависал в Forming навсегда после crash — найдено S-4 E2E) | Functional `d11_delete_during_upload_then_worker_failure_still_cleans_bytes` (точная трасса проверки: реальный upload → delete → sweep в lease → сбой после upload → sweep снимает объект без участия воркера), `d11_frozen_worker_aborts_upload_after_delete`, обновлённые c2/c3; E2E `S-3` (реальный pause/kill процессов: delete 204, воскрешения нет, intent снят) и `S-4` (kill воркера между upload и Ready → Ready, 115 632 строки == БД) |
| D13 действенность тестов | частично | M5-защита в Functional+HTTP (user → чужой masterId: 403, кошелёк жертвы нетронут, сервисный грант — позитивный контроль); EDGE-02 переписан: два задания одного стрима, ДВА параллельных завершения через барьер, пороги 10/20 одного достижения, очки 25 = 10+15; `ScriptedImpediments`: сигнал достижения фазы (`WaitReachedAsync`), жёсткая пауза без авто-отпускания (защитный 120 c FAIL, не «успех»), one-shot пауза для одного воркера; C1/C2 без `Task.Delay`-синхронизации | Прогоны ниже; пробы m1–m6: 5/6 ловятся бизнес-assertions (балансы/исходы/история), m3 — БД-ограждением `ux_operations_single_reversal` (деньги защищены вторым слоем; тест падает на DbUpdateException, не молчит) |
| D16 границы и метрики | частично | Авторизация операции полностью в Application; middleware получает `IRequestMetrics` (Application-интерфейс) — исключение `ProblemDetailsMiddleware` из архитектурного теста УДАЛЕНО; DB-метрики — реальные: `DbCommandTimingInterceptor` меряет каждую команду EF (`eventData.Duration`), рендер разделён (RequestLatencies/DbLatencies — разные выборки) | Architecture 5/5 без исключений; E2E «metrics on controlled traffic»: read-count вырос на контролируемых GET (одна реплика), `motiva_db_duration_ms{kind="SELECT"/"INSERT"} — реальные замеры (артефакт `artifacts/load/metrics-after-load.txt`) |
| D18 синтаксис test.sh | регрессия R1 | Скобки `${…:-${…:-…}}` восстановлены; `bash -n` всех 9 скриптов; чистый checkout: restore → migrate (отдельная пустая БД, M1→M4) → `jwt.py prepare` → bootstrap → `test.sh unit/http` — exit 0. Найден и исправлен дефект чистого старта: `jwt.py ensure_project` не создавал appsettings*.json, без которых `dotnet user-jwts` падает | `bash -n` вывод; логи прогона в чистом клоне `/tmp/motiva-r2-clean` (exit 0) |
| D15/T09 | частично | Основной профиль: общий `load/mix.js` (50/25/10/10/5), seeded PRNG (mulberry32, SEED=42, per-VU init — найден и исправлен дефект пересоздания PRNG на каждой итерации: VU застывал на одной ветке/одном кошельке); goodput = Rate «корректный бизнес-результат ≤1 c» с порогом ≥99,5 %; счётчики events/spends для сверки. Деградация — ТОт же профиль 100 RPS 60+300 c (Valkey 30 c / S3 30 c / API-2 10 c). Конкуренты: выделенная race-пара из seed (бюджет ровно 10 = награде, публикация), агрегация через k6 Counter-пороги (провал роняет команду), VU-массивы убраны. Верификатор `load_verify.py` сверяет k6-счётчики с БД в окне прогона | §3 ниже: exit codes, planned/sent/completed, goodput, p95, гонка 1/1/0, сверка k6==БД |

Дополнительные дефекты, найденные и исправленные в R2 (сплошная проверка «соседних путей»):

1. **StrictDates сдвигал все «Z»-даты на офсет машины** (литерал `'Z'` в формате → время
   читалось как локальное: на хосте UTC+3 `2026-01-01T00:00:00Z` превращалось в
   `2025-12-31T21:00:00+00`). Исправлено спецификатором `K` + явная проверка наличия офсета
   (`HTTP StrictDatesZuluTests`: парсер возвращает точный инстант; годовая кампания — один сезон).
2. **Form-export job завершался молча при живом lease** (после crash воркера экспорт зависал в
   Forming навсегда). `HandleAsync` возвращает признак завершённости; RetryAt+15 c (E2E S-4).
3. **Чистый старт `auth/jwt.py prepare`** — см. D18.

## 1. Команды и результаты (финальный commit)

| Команда | Результат |
|---|---|
| `dotnet build Motiva.sln --no-restore` | 0 warnings, 0 errors |
| `scripts/test.sh unit` | 40/40 |
| `scripts/test.sh architecture` | 5/5 (+ двойной гейт `verify-arch-gate.sh` — RS0030 и NetArchTest срабатывают на подсаженных нарушениях) |
| `scripts/test.sh persistence` | 7/7 (реальный PG) |
| `scripts/test.sh functional` ×3 | 66/66 три раза подряд (`artifacts/r2/functional-x3.log`) |
| concurrency-класс ×10 | 6/6 десять раз (`artifacts/r2/concurrency-x10.log`) |
| `scripts/test.sh http` | 30/30 |
| `scripts/e2e/run.sh` | 28 проверок, 0 отказов (`artifacts/e2e/run-final.log`) — вкл. replay через рестарт API, финализация ≤5 c, Valkey/S3/PG/API-отказы, экспорт bytes immutable, **S-4 kill воркера между upload и Ready (восстановление, 115 632 строки == БД)**, **S-3 реальный замороженный воркер против delete (фенсинг, без воскрешения, intent снят)**, метрики на контролируемых запросах |
| `dotnet format Motiva.sln --verify-no-changes` | exit 0 |
| Чистый checkout (`/tmp/motiva-r2-clean`, без bin/obj) | restore → migrate (пустая БД `motiva_r2_clean`, M1→M4) → `python3 auth/jwt.py prepare` → `bootstrap.sh` (exit 0) → `test.sh unit`/`http` (exit 0) |

Смысловые пробы (по одной в изолированной ветке, `artifacts/r2/probes/`):

| Проба | Мутация | Исходный сценарий | Результат |
|---|---|---|---|
| m1 | отказ при равенстве бюджета (`<` → `<=`) | проходит | ПАДАЕТ на исходе награды (обнаружена) |
| m2 | сохранённый Declined повторно исполняется (оба replay-шва только Posted) | проходит | ПАДАЕТ на балансе (обнаружена) |
| m3 | снята защита повторного возврата в сервисе | проходит | ПАДАЕТ на БД-огражчении `ux_operations_single_reversal` — деньги защищены вторым слоем; двойной эффект невозможен, тест не зелёный |
| m4 | рейтинг считает переданный delta вместо зачтённого | проходит | ПАДАЕТ на счёте (обнаружена) |
| m5 | user списывает чужой masterId | проходит | ПАДАЕТ: AuthzForbidden-тест видит Posted и изменение чужого баланса (обнаружена) |
| m6 | компилируемый endpoint с зависимостью от Infrastructure (не banned-тип) | — | ПАДАЕТ архитектурный тест (обнаружена) |

После каждой пробы исходник восстановлен побайтно (branch deleted, `git checkout base -- src/ tests/…`), сборка восстановлена.

## 2. T09 (D15) — обязательный профиль

Стенд: macOS arm64 12 CPU/18 GB; compose: 2×API + 2×worker + LB + PG 17.9 + Valkey 9.2 + MinIO;
генератор k6 2.3.0 в контейнере той же сети (ограничение генератора не объявляется пределом
приложения). Подготовка seed=42 вне измерения: 10 000+100 сотрудников, 3 ресурса, 10 кампаний ×
10 заданий, 100 000 событий (завершения 9,0 %), 100 000 движений, все кошельки пополнены,
уникальные номера операций; race-пара: goal 3 / награда 10 / бюджет ровно 10 (опубликована).

**Основной профиль** (ramping-arrival-rate 1→100/с за 60 c + 100/с 300 c; смесь 50/25/10/10/5;
`artifacts/load/main.{log,summary.json,report.json}`, `stats-main.csv`):

- planned ≈ 33 030 итераций; **отправлено 33 029 HTTP-запросов; завершено 33 029 итераций; dropped 0**;
- **goodput 99,994 %** (33 299/33 301 запросов дали корректный бизнес-результат в пределах 1 c;
  Rate-метрика на запрос, не на check; порог ≥99,5 % пройден, k6 exit 0);
- p95 чтение **4,70 мс** (цель ≤200), p95 запись **10,42 мс** (цель ≤500), max 30,64 мс →
  p99 < 1 с (цель ≤1000) — по построению p99 ≤ max;
- бизнес-проверки: события `result=Accepted`, траты `result=Posted` (Declined считается провалом);
- ресурсов: ряды `docker stats` каждые 10 c за весь прогон (`stats-main.csv`: api/worker/postgres/k6/valkey/s3);
  реальные задержки БД за прогон — `metrics-after-load.txt`: SELECT p95 0,68 мс, INSERT p95 0,28 мс
  (измерены перехватчиком команд, не HTTP-замерами).

**Сверка ожидаемых и сохранённых эффектов** (`scripts/load_verify.py`, окно прогона):

- события: k6 accepted **3345** == сохранено **3345**; траты: k6 Posted **1706** == сохранено **1706**;
- дублей LOAD-номеров 0, дублей номеров событий 0, отрицательных бюджетов 0, отрицательных балансов 0.

**Сценарий двух конкурентов за последний остаток** (бюджет ровно 10, награда 10):
k6 exit 0 — агрегированные Counter-пороги `granted==1`, `declined==1`, `bad==0` пройдены;
SQL: Posted 1 / Declined 1 / остаток 0 (не −10).

**Деградационный проход** — тот же профиль 100 RPS 60+300 c, та же смесь из общего `mix.js`;
Valkey pause 30 c (внутри измерения), S3 stop 30 c, один API stop 10 c
(`degradation.{log,summary.json}`, `stats-degradation.csv`): 33 002 запросов, dropped 28,
goodput 99,65 % (32886/33002 корректных в ≤1 c — отказы только в окнах отключений),
восстановление без ручного вмешательства. PostgreSQL отключается отдельно в E2E:
readiness 503 + Retry-After, liveness 200, запись 503, после восстановления replay идентичен
(и это не выдаётся за бизнес-доступность).

**Экспорт 100k+** (`export100k.log`): Ready за 30,2 с (цель ≤120), 15,06 МБ, 120 867 строк ==
числу проведённых движений в диапазоне, checksum совпал при скачивании.

## 3. Остаточные ограничения (честно)

1. 250/500/1000 RPS (опциональная часть T09) — не проверено.
2. Проба m3 обнаруживается БД-ограждением, а не сервисным assertion баланса (деньги защищены;
   тест красный — двойной эффект невозможен, но диагностика через 500/DbUpdateException).
3. Деградационный goodput 99,65 % ниже основного (99,994 %) — это ожидаемо для окон отказов
   и не является гейтом; гейты — в основном профиле.
4. Сгенерированный p99 в summary-export отсутствует (порог p99 проверялся k6-порогом;
   из max=30,64 мс следует p99 ≤ 30,64 мс < 1000 мс).

---

# R1 (F) — первый раунд исправлений (история; commit f91d953)

Статус: **F** — первый раунд исправлений R1 после внешней оценки R0 (26/100, gate FAIL).
R0 сохранён в истории Git (commit c9912e2) и в §7 ниже. Все пункты обратной связи H1
(восемь групп дефектов D01–D17) закрыты в этом раунде; команды, exit codes и исходные
отчёты обновлены. Секреты и токены в Git не попадали.

## R1.0 Коммит F

`f91d9539d5c7c7e628ce393a53130be2bb540cb1` (R0-коммит `c9912e2` не переописан).

## R1.1 Изменения по пунктам H1 (D01–D17)

| Пункт H1 / дефект | Что изменено | Проверки |
|---|---|---|
| 1 (D01) Блокировка инициатора не закрывает действия | Новый `CurrentRights` (Application/Common): `EnsureAdminAsync`/`EnsureActiveEmployeeAsync` требуют активный профиль во ВСЕХ use cases (записи и чтения, включая replay); админ-команды больше не доверяют только роли | Functional `AccessRightsR1Tests.d01_*`; HTTP-негатив |
| 1 (D02) JWT identity | `JwtSetup.IdentityClaimsAreConsistent`: обязательный непустой `sub`; user обязан иметь роль Employee (+опц. Admin), service обязан быть без ролей; `Authz.EnsureAdmin` — только User | HTTP `jwt_without_subject`, `jwt_user_without_role`, `jwt_service_with_admin_role` |
| 1 (D04) Бывший владелец получает replay | `ManualAwardsService`: проверка владения/гранта поднята ВЫШЕ поиска номера (§3.0) | Functional `d04_former_owner_gets_403_before_replay` |
| 2 (D03) Экспорт без текущего права | `EnsureRequesterAsync`: строго заказчик + активный профиль; scope Own — любой активный, Employee/Company — действующий Admin; список для Admin остаётся по OpenAPI, но ресурс (статус/ссылки) только у заказчика | Functional `d03_export_scope_requires_current_admin_role` |
| 2 (D05) Детали контента вне аудитории | `EnsureContentVisibleAsync` + `EnsureTaskAudienceAsync`: detail-чтения стримов/заданий/вех/челленджов требуют активность и аудиторию кампании (и задания) | Functional `d05_task_details_outside_audience_are_forbidden` |
| 3 (D06) Архив lifecycle | Архивный ресурс запрещён в acceptedResourceIds purchase system; PATCH архивного задания/стрима — 409 (сервис + store-уровень InvalidTransition) | Functional `d06_archive_is_irreversible_and_rejected_in_new_settings` |
| 3 (D17) Отказ награды не в истории операций | `DecideRewardAsync`: Declined-ветки создают сохранённую операцию TaskReward (Declined, попытленные позиции) и возвращают её operationId в completion.reward | Functional `d17_declined_reward_is_a_saved_operation`; E02 в HTTP-группе |
| 4 (D07) If-Match неатомарен | Все PATCH/PUT/DELETE настроек: `SELECT … FOR UPDATE` на строку до чтения версии + `ChangeTracker.Clear()` после блокировки (второй конкурент видит новую версию → 412) | HTTP `concurrent_same_if_match_patch_yields_single_200` (4 раунда по 6 параллельных PATCH: ровно 1×200 + 5×412) |
| 4 (D08) Неполные существенные данные | Stream: {code,name}; Task: {code,name,description,goal,period,streamPoints,items,audience}; replay-гейт выполняется ДО проверки Draft (изменение родителя не блокирует повтор) | Functional `d08_changed_essential_data_with_same_key_conflicts` |
| 4 (D09a) Пагинация каталога | Честный offset-курсор с nextCursor только при наличии следующей страницы | HTTP `catalog_pagination_reaches_all_items` |
| 4 (D09b) Пагинация достижений | Курсор (season, grantedAt, achievementId) с уникальным tie-breaker; ORDER BY включает achievementId | Functional (48 достижений, страницы размера 1, без потерь) |
| 4 (D09c) Binding/ошибки | ProblemDetails-конвейер: пустые 4xx тела фреймворка добиваются ProblemDetails (code+traceId); enum-парсинг через `EndpointHelpers.ParseEnum` (невалидное значение → 400 validation.failed, не 500) | HTTP `invalid_enum_and_non_integer_amount_are_contract_400s` |
| 4 (D09d) Даты | `StrictDates`: все входные даты (campaign, challenge, export, query from/to) — строки с обязательным UTC-offset; без offset → 400; с ненулевым offset → нормализация в UTC | HTTP `campaign_dates_without_utc_offset_are_400`, `..._normalized_to_utc` |
| 4 (D09e) DTO Actor | actorType сериализуется как `user`/`service` (нижний регистр, по OpenAPI) | HTTP `actor_dto_uses_lowercase_enums` |
| 4 (D09f) Location при replay | `StoredEcho.Location`: replay создания возвращает исходный Location (id из сохранённого тела) + 201 + то же тело | HTTP `replay_of_create_keeps_location_header` |
| 5 (D10) Outbox Running после crash | Claim захватывает и `status='Running' AND locked_until < now` (мёртвый worker теряет lease — задача возвращается) | Functional `d10_running_job_with_expired_lease_is_reclaimed` |
| 5 (D11) Cleanup против позднего upload | (а) `HasLiveLeaseAsync`: sweep не снимает intent при живом lease; (б) handler перед upload проверяет Deleted; (в) после upload при Deleted удаляет свой объект и не делает Ready | Functional `d11_cleanup_waits_for_live_lease_and_late_upload_removes_itself` |
| 5 (D12) Экспорт не потоковый | Полностью переписан: `SnapshotRowSource` — серверский keyset-курсор по (createdAt,operationId,resourceCode); `S3FileStorage.PutPartsAsync` — multipart по 8 МБ частей; CSV пишется частями, checksum инкрементальный; вся выписка не материализуется | export100k: 115 626 строк за 34.3 с, 14.6 МБ, checksum при скачивании совпал |
| 6 (D13) Тесты | (а) неверные ожидания заблокированного чтения заменены на 403 + чтение через Admin; (б) добавлена защита запрета чужого кошелька (M5-сценарий) в Functional+HTTP; (в) C1 экспорта — реальный поздний commit через новый шов `ManualAwardBeforeCommit`; (г) F2 — реальный конкурирующий финализатор (assert: не завершился до release); (д) EDGE-02 — две параллельные завершения двух заданий одного стрима; (е) C2 — через штатный `ExportFormationHandler` (worker A пауза после upload → fake-clock +61 с lease → worker B захватывает gen=2 → Ready; A возобновляется, CAS отклоняет); (ж) E2E: уникальные masterId за прогон, docker restart по именам контейнеров, Idempotency-Key как header, проверка непустого URL/CSV до checksum | Functional/HTTP/E2E-группы ниже |
| 7 (D14) Clean start | `scripts/migrate.sh` и `bootstrap.sh` выполняют restore+build до запуска (чистый checkout без bin/obj работает); precedence env исправлен; сломанный `deploy/migrate.Dockerfile` удалён из compose | Проверено в отдельном чистом клоне: migrate + bootstrap exit 0, 2 компании + 2 админа |
| 7 (D16) Границы/наблюдаемость | Бизнес-авторизация из `OperationsReadStore` перенесена в `ReadService` (Infrastructure — чистая фильтрация); `BannedSymbols.txt` подключён как AdditionalFiles → RS0030 compile-time гейт (проверен пробой); метрики реально считаются (ProblemDetailsMiddleware: latency по kind/phase, статус-каунтеры; worker: outbox depth) и рендерятся в Prometheus-текст с квантилями; sync-IO `Task.FromResult(…ToList())` устранён | `scripts/verify-arch-gate.sh` — двойной гейт (RS0030 + NetArchTest), оба срабатывают; /metrics |
| 8 (D15) Нагрузка | main.js: ветка leaderboard исправлена (было двойное индексирование UUID — TypeError), challenge-набор проверяется до запуска (k6env.sh падает при пустом), бизнес-проверки результата (Accepted/Posted), warmup-тег фаз; seed создаёт челленджи до публикации и пополняет ВСЕ кошельки; competitors — строгие исходы (1 Granted + 1 Declined, credited==goal) + DB-сверка; export100k скачивает и сверяет строки с БД; degradation — отдельный проход той же смесью с паузами зависимостей | Ниже §4 |

## 1. Команды из чистого checkout (фактические exit codes — artifacts/final-run.log)

```bash
python3 auth/jwt.py prepare   # exit 0 (при первом запуске в новом клоне возможен quirk обёртки — см. README)
docker compose up -d          # PG 17.9 + Valkey 9.2 + MinIO + LB + 2×API + 2×worker; healthy
scripts/migrate.sh            # exit 0 (restore+build+M1→M4; проверено и в отдельном чистом клоне)
scripts/bootstrap.sh          # exit 0
scripts/test.sh all           # unit 40, arch 5, persistence 7, functional 55, http 24, e2e 17 — все зелёные
scripts/verify-arch-gate.sh   # exit 0: RS0030 compile-time ban + NetArchTest, оба срабатывают на подсаженных нарушениях
scripts/load.sh all           # seed + k6 main + competitors + export100k + degradation
dotnet format Motiva.sln --verify-no-changes   # exit 0
```

Исходные отчёты: `artifacts/final-run.log`, `artifacts/final-run.load.log`,
`artifacts/load/{main,competitors,degradation,seed}.log`, `artifacts/e2e/{passed,failures}.log`,
`artifacts/arch-violation.log`.

## 2. Тестовые группы (F)

| Группа | Результат |
|---|---|
| Unit | 40/40 |
| Architecture | 5/5 (+ двойной mutation-гейт) |
| Persistence | 7/7 (пустая БД + пошагово M1→M4, CHECK, partial unique) |
| Functional | 55/45→55 (все R0-тесты + 10 новых R1-регрессионных) |
| HTTP | 24/24 (14 R0 + 10 R1: JWT-негатив, конкурентный If-Match, даты, enum, пагинация, Location-replay) |
| E2E (живой стенд) | 17/17 |

## 3. Результаты нагрузки F (стенд: macOS arm64 12 CPU/18 GB; лимиты контейнеров по T09)

Основной профиль (k6 2.3.0, открытая модель ramping-arrival-rate 1→100/с за 60 с + 100/с 300 с;
метрики окна измерения отделены тегом phase=measure):

- planned 100 RPS; фактически 91.7 iterations/s и 91.7 http_reqs/s (33 030 запросов);
- **goodput 99.997 %** (37 993/37 995 checks; единственный сбой — один progress-event);
- p95 чтение **5.19 мс** (цель ≤200), p95 запись **13.97 мс** (цель ≤500), p99 **23.56 мс** (цель ≤1000);
- business checks: события `result=Accepted`, траты `result=Posted` (declined считается нарушением);
- ресурсы: API 11.5 %/8.5 % CPU и 125/139 МБ, PG 11 %/197 МБ, Valkey 12 МБ, S3 391 МБ, workers <1 %;
- итоговые бизнес-состояния (SQL-сверка): отрицательных бюджетов 0, отрицательных балансов 0,
  дублей Posted-отмен 0, дублей бизнес-номеров 0, дублей номеров событий 0.

Сценарий двух конкурентов (бюджет 10, награда 10): **ровно 1 Posted + 1 Declined**, бюджет 0 (не −10),
credited==goal у победителя, 6/6 checks зелёные + DB-сверка.

Выписка 100k+ (Company-scope): **Ready за 34.3 с** (цель ≤120), 115 626 строк == числу проведённых
движений в диапазоне (SQL-сверка), 14.6 МБ, SHA-256 скачанных байтов совпал с метаданными.

Degradation-проход (та же смесь, 50 RPS, 120 с; Valkey pause 30 с, S3 stop 30 с, один API stop 10 с):
**checks 100 %** (5967/5967), p95 407 мс — деградация задержек при отказах зафиксирована,
восстановление без ручного вмешательства.

## 4. Остаточные ограничения (честно)

1. Единственный сбой основного профиля (1/33 030) — progress-event не вернул 201; тело ответа не
   сохранено k6; SQL-сверка дублей/потерь не нашла. goodput 99.997 % ≥ 99.5 % цели.
2. 250/500/1000 RPS (опциональная часть) — не проверено.
3. Токены 1 час: перед длинными прогонами повторять `python3 auth/jwt.py prepare` (quirk обёртки
   при первом запуске в новом клоне описан в README).
4. Пробы мутаций проверяющего (M1–M6) выполнялись против R0; в F добавлен явный тест запрета
   чужого кошелька (M5-сценарий) и все R0-пройденные пробы остались зелёными (функциональные
   группы прогнаны полностью после правок).
5. Degradation-проход использует 50 RPS/120 с (не основной профиль 100/300) — фактическая
   деградация зафиксирована, но SLO-пороги формально применялись к основному окну.


---

# R0 — исходная сдача (сохранено для истории)

Статус: первая сдача для внешней оценки. Все обязательные правила этапов 1–3 реализованы
без сужения объёма; известные отклонения перечислены отдельным списком. Секреты и токены
в Git не попадали (`auth/.local/` в `.gitignore`, ключ в окружении API).

## 1. Коммит

См. `git log -1` в репозитории (этот документ коммитится вместе с кодом).

## 2. Реализованные B/T-пункты

Полный код: `src/Motiva.{Domain,Application,Infrastructure,Api,Worker}`; тесты: `tests/`;
нагрузка: `load/`, `scripts/load.sh`; живой стенд: `docker-compose.yml`, `scripts/e2e/`.

| Пункт | Реализация | Тесты |
|---|---|---|
| B01–B02 | `employees` PK (company,masterId), кошелёк создаётся той же транзакцией (`EmployeesService.CreateAsync`, `EmployeeStore.CreateAsync`); тенант-фильтр во всех сторах | Functional `VerticalTests`, HTTP `ContractTests.cross_company_objects_are_404_e12` |
| B03–B05 | Матрица прав в сервисах (`Authz`, `CampaignsService.EnsureCanConfigure`, гранты `IntegrationGrantsService`); мгновенная актуальность (`SCN-06` тест) | Functional `EconomyRightsTests` (SCN-06, owner change, grant revoke) |
| B06–B07 | `ResourcesService` (нормализация кода в верхний регистр, архивация), `ResourceStatus.Archived` | Unit `CodesTests`; Functional `r_pr_resource_unavailability_outranks_budget_shortage_q02`, `edge03` |
| B08–B09 | `CampaignsService` + `CampaignStore`: публикация с проверками AMB-12, матрица мутабельности §4.5, надгробие Deleted освобождает код | Functional `VerticalTests`, persistence `Campaign_code_unique_excludes_deleted_tombstones` |
| B10–B11 | `PeriodCalendar`/`Seasons` (NodaTime, Europe/Tallinn), аудитория any/all/none кампании и задания | Unit `PeriodCalendarTests`, `AudienceRuleTests`; Functional `TimeBoundariesTests` (EDGE-01/05), `ChallengeTests.q04` |
| B12–B13 | `CampaignContentService.CreateTaskAsync` (валидация наград из набора кампании), календарные периоды | Unit `PeriodCalendarTests`; Functional `TimeBoundariesTests` |
| B14–B15 | `ProgressEventsService.PostAsync` (одно чтение времени после замков §3.2, min-правило, уникальные завершения) | Functional `VerticalTests` (E03, R-ИД), `TimeBoundariesTests` (SCN-05) |
| B16–B17 | Канонические данные+ответ в `progress_events` (байт-в-байт replay), единая транзакция вертикали | Functional `VerticalTests.replay_after_commit...`, `technical_failure_before_commit...` |
| B18–B19 | `BudgetService.AllocateAsync` (Admin, окна Q09), `budgets` CHECK + FOR UPDATE | Persistence `Budget_check_rejects_negative_available_b19d`; Functional `ConcurrencyTests` (E04) |
| B20 | Пакетная награда целиком/ничего, финальность отказа, приоритет недоступности (H0 Q02) | Functional `e02_shortage...`, `r_pr...` |
| B21 | `ManualAwardsService` (владелец/Admin/grant Award; Declined Recipient*) | Functional `manual_award_requires_audience...` |
| B22–B23 | `WalletStore` (CHECK balance≥0), `SpendsService` (свой кошелёк / сервисная трата) | Functional `EconomyRightsTests.scn03`, `ConcurrencyTests.e05` |
| B24–B25 | Уникальность (company, initiator, kind, number), 409 при иных данных, неизменяемость операций | Persistence `Business_number_unique...`; Functional replay-тесты |
| B26–B27 | `ReversalsService` (полная отмена пакетов/трат; H0 Q03 только Admin для заблокированных) | Functional `e08`, `edge03`, `reversal_rights...`, `e07` (параллельные возвраты) |
| B28 | Единая книга `operations` (+items), аудит `audit_records` с инициатором и временем | HTTP/Functional чтения; `scripts/e2e` |
| B29–B30 | `stream_points` + совместная проверка вех, `achievement_grants` PK ≤1/сезон | Functional `e10`, `edge02` |
| B31–B33 | Челленджи, advisory-замки `CompetitionStore`, финализатор `FinalizeChallengeHandler` | Functional `ChallengeTests` (Ф-1…Ф-5, EDGE-04); E2E «finalized by worker in 4s» |
| B34 | `/me/*`, `/employees/{id}/wallet|operations` (Admin), `/campaigns/{id}/operations` (владелец), пагинация | Functional `EconomyRightsTests.service_reads_only_own_spend_pairs`; HTTP `list_envelope...` |
| B35–B37 | `ExportsService` + `ExportStore` (снимок B36.2, generation-CAS, S3 put-if-absent, ссылки ≤60 с, удаление + cleanup) | Functional `ExportTests` (SCN-07, C-0…C-3); E2E immutable bytes |
| T01 | PostgreSQL 17.9, Valkey 9.2, MinIO (digest), SDK 10, lock-файлы, миграции только PG | Persistence `MigrationsTests` (пустая БД + пошагово M1→M4) |
| T02 | Проекты/зависимости §1.2, BannedApiAnalyzers + NetArchTest, группировка по функциям | Architecture 5/5 + `verify-arch-gate.sh` (компилируемое нарушение роняет группу) |
| T03 | JwtBearer HS256 (ключ из Base64-байтов), дубликаты claims → 401, bootstrap-команда | HTTP `AuthNegativeTests` (8 кейсов); `scripts/bootstrap.sh` |
| T04 | 44 маршрута/70 методов по `openapi.yaml` 1.2.0, ProblemDetails+code+traceId, strong ETag 428/412, Declined=201 | HTTP `ContractTests` (ETag-цикл, Declined, пагинация, Location) |
| T05 | Idempotency-Key ≥24 ч (`IdempotencyStore`), TimeProvider, точная арифметика (`SafeMath`) | Functional replay-тесты; Unit `SafeMathTests` |
| T06 | 2×API+2×worker, outbox `FOR UPDATE SKIP LOCKED`, 503+Retry-After при недоступности PG, Valkey-деградация | Functional `OutboxTests`; E2E (Valkey/S3/API/PG проходы) |
| T07 | CSV T07 (потоково), снимок REPEATABLE READ, ссылки ≤60 с, cleanup ≤10 мин, финал ≤5 с | Functional `ExportTests`; E2E; нагрузка export100k |
| T08 | Слои: unit 40, architecture 5, persistence 7, functional 45, http 14, e2e 17 — все зелёные | `artifacts/final-run.log` |
| T09 | Профиль нагрузки: seed=42 (10 000+100, 3 ресурса, 10×10, 100k событий + 100k движений), k6 открытая модель | `artifacts/load/main.log`, `competitors.log` |
| T10 | Команды из чистого checkout, `.env.example`, README ≤2 стр., примеры Employee/Admin/Integration | `README.md`, `docs/examples/` |

## 3. Команды из чистого checkout (фактические exit codes — `artifacts/final-run.log`)

```bash
python3 auth/jwt.py prepare   # exit 0 (нужен .NET 10 SDK; до старта API — обязательно)
docker compose up -d          # PG 17.9 + Valkey 9.2 + MinIO + LB + 2×API + 2×worker; healthy
scripts/migrate.sh            # exit 0 (M1→M4)
scripts/bootstrap.sh          # exit 0
scripts/test.sh all           # unit 40/40, arch 5/5, persistence 7/7, functional 45/45, http 14/14, e2e 17/17
scripts/verify-arch-gate.sh   # exit 0 (подсаженное нарушение роняет группу)
scripts/load.sh all           # seed + k6 main + competitors + export100k
dotnet format Motiva.sln --verify-no-changes   # exit 0
```

Исходные отчёты: `artifacts/final-run.log`, `artifacts/final-run.load.log`,
`artifacts/load/{main,competitors,seed}.log`, `artifacts/e2e/{passed,failures}.log`,
`artifacts/arch-violation.log`.

## 4. Результаты нагрузки (стенд: macOS arm64, 12 CPU/18 GB; лимиты контейнеров по T09)

Основной профиль (k6 2.3.0, открытая модель, 60 с прогрев + 300 с измерение):

- запланировано 100 RPS, фактически `iterations` 91.7/с, `http_reqs` 82.9/с (29 865 запросов);
- goodput ≥99.5 %: **99.997 %** (29 864/29 865; единственный сбой — один POST события);
- p95 чтение **4.45 мс** (цель ≤200), p95 запись **14.91 мс** (цель ≤500), p99 **18.31 мс** (цель ≤1000);
- CPU/RAM за измерение: API ~10 %/162 МБ и ~9 %/153 МБ (лимит 1 CPU/1 GiB), PG ~5 %/52 МБ,
  workers <1 %/253 МБ, Valkey 12 МБ; генератор k6 в отдельном контейнере (1 VU-пул, ~0 % после пика);
- сверки итоговых бизнес-состояний: отрицательных бюджетов **0**, отрицательных балансов **0**,
  потерянных/дублированных номеров нет (уникальный индекс + replay-тесты).

Сценарий двух конкурентов (последний остаток бюджета 10, награда 10): ровно **1 Posted**,
бюджет **0** (не −10), 4/4 проверки зелёные (`artifacts/load/competitors.log`).

Выписка 100 000 строк: **Ready за 12.1 с** (цель ≤120 с), 12.85 МБ, checksum D198FDAFBF1EDA6F.

Деградационные проходы (E2E, `scripts/e2e/run.sh`): Valkey pause → каталог/рейтинг из PG (200);
один API вниз 10 с → 10/10 запросов; PG вниз → readiness 503, liveness 200, запись 503+Retry-After,
после восстановления replay без дублей; S3 вниз → учёт прогресса работает, ссылка → 424.

Доля завершений в seed: 9.0 % (9000/100 000, seed=42); распределение состояний в `artifacts/load/seed.log`.

## 5. Известные отклонения и ограничения

1. **Единственный сбой в нагрузке** — 1 из 29 865 запросов (POST события, не 201). Причина в
   логах не зафиксирована (k6 не сохраняет тело); replay-инварианты подтверждены сверкой БД.
2. **Фактический RPS 82.9 при плане 100**: пул k6 VU (50 pre-allocated) при времени итерации
   ~49 мс не полностью покрыл целевую интенсивность; порог goodput и задержек выполнен с запасом.
3. **250/500/1000 RPS не прогонялись** (опциональная часть T09) — «не проверено».
4. `scripts/test.sh e2e` требует поднятого compose-стенда и свежих токенов (обёртка выдаёт их
   на 1 час — перед длинными прогонами повторите `python3 auth/jwt.py prepare`).
5. k6-контейнер работает в той же compose-сети (генератор не на отдельной машине — ресурсы
   стенда фиксировались, но не изолированы полностью).
6. Тестовые токены HTTP-группы подписаны тестовым ключом из окружения (разрешено 03_AUTH);
   негативный набор покрыт полностью, но на «рабочем» ключе стенда, а не на ключе из `api.env`.
7. Слой E2E — bash-сценарии (`scripts/e2e/run.sh`) вместо отдельного .NET-проекта: те же
   проверки на живом стенде; результаты в `artifacts/e2e/`.
8. Минорное: `motiva_failed_requests` в первом прогоне нагрузки показывал 2.8 % из-за ошибки
   самого скрипта k6 (сервисная трата с чужим masterId от user-токена → 403); после исправления
   скрипта итоговый прогон зелёный. Оба лога сохранены.

## 6. Проверки бизнес-эффектов перед сдачей

- Две компании с одинаковым masterId=123: разные кошельки, кросс-доступ 404 (HTTP `e12`).
- Два ресурса в одной награде: оба остатка бюджета 0, кошелёк 10/2 (Functional `e01`).
- Повтор сохранённого отказа: replay байт-в-байт, пополнение не переигрывает (`e02`, SCN-03).
- Конкурентный бюджет/кошелёк/возврат: E04/E05/E07 — ровно одна Posted, остаток 0/3/+7 один раз.
- Повтор после рестарта: идентичный ответ (E2E `idempotent replay identical after API restart`).
- Неизменяемый файл экспорта: checksum совпадает между скачиваниями (E2E `export bytes immutable`).

После первой сдачи остановлено для внешней оценки.
