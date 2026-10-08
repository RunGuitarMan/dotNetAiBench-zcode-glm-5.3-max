# Motiva

Внутренняя система мотивационных кампаний: сотрудники выполняют задания, получают ресурсы и
достижения, участвуют в соревнованиях. Стек: .NET 10 / ASP.NET Core, EF Core 10 + Npgsql,
PostgreSQL 17, Valkey 9, приватный S3 (MinIO). Версии пакетов и образов зафиксированы
(`Directory.Packages.props`, `docker-compose.yml`, lock-файлы).

## Запуск из чистого checkout

Порядок обязателен: шаг 1 создаёт `auth/.local/api.env`, которого нет в Git — без него API
не стартует (явная диагностика compose).

```bash
python3 auth/jwt.py prepare   # 1) тестовый ключ + токены (нужен .NET 10 SDK)
docker compose up -d          # 2) PG 17, Valkey 9, S3, LB, 2×API, 2×worker
scripts/migrate.sh            # 3) миграции M1–M4 на PostgreSQL 17
scripts/bootstrap.sh          # 4) компании + активные администраторы (use case)
```

Smoke: `curl -H "Authorization: Bearer $(cat auth/.local/tokens/employee123.jwt)" http://localhost:8080/api/v1/resources`.

## Команды

| Команда | Назначение |
|---|---|
| `scripts/test.sh unit\|architecture\|persistence\|functional\|http\|e2e\|all` | группы тестов (T08); e2e — сценарии на живом стенде |
| `scripts/verify-arch-gate.sh` | подсаженное компилируемое нарушение роняет арх-группу |
| `scripts/load.sh seed\|main\|competitors\|export100k\|degradation\|all` | подготовка профиля T09 и k6 |
| `dotnet format Motiva.sln --verify-no-changes` | форматирование |

Инфраструктура тестов — тот же compose-стенд (`localhost:5433/6380/9100`); тесты создают свои
базы данных и применяют миграции (это допустимая инфраструктурная подготовка), бизнес-данные —
только через use cases/HTTP.

## Примеры запросов

`docs/examples/` — Employee, Admin, Integration сценарии (curl). Токены: `auth/.local/tokens/`.
API: `http://localhost:8080/api/v1` — контракт `docs/openapi.yaml` (44 маршрута / 70 методов).

## Структура

`src/Motiva.Domain` (чистые правила) → `Motiva.Application` (use cases, порты) ←
`Motiva.Infrastructure` (EF/Valkey/S3/outbox) ← `Motiva.Api` / `Motiva.Worker` (composition
root). Тесты: `tests/` по слоям; нагрузка: `load/`; сценарии живого стенда: `scripts/e2e/`.
Требование → реализация/тест: `docs/stage-4.md`.

## Ограничения стенда

- Токены `jwt.py` действуют 1 час: долгие прогоны обновляйте `python3 auth/jwt.py prepare`.
- Особенность обёртки (вход этапа, не изменялся): при первом запуске `prepare` в новом клоне
  служебный проект создаётся без `appsettings*.json` и команда завершается ошибкой — выполните
  `echo '{}' > auth/.local/issuer/appsettings.json && echo '{}' > auth/.local/issuer/appsettings.Development.json`
  и повторите `prepare` (ключ и токены создаются штатно).
- Секреты только в `auth/.local/` (вне Git); `.env.example` без секретов.
