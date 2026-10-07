# Stage 4 — реализация Motiva

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
