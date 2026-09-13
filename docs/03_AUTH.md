# Тестовая аутентификация — один договор для всех
Не identity-сервис. Выпуск токенов выполняется вне Motiva; backend только проверяет их.

## Claims и проверка
| Поле | Смысл |
|---|---|
| iss / aud | `motiva-benchmark` / `motiva-api` |
| sub | Постоянный идентификатор инициатора; не произвольный заголовок запроса |
| companyId | UUID компании; задаёт границу доступа |
| actorType | `user` или `service` |
| masterId | Положительный Int32, обязателен только для user |
| role | `Employee`; дополнительный `Admin` только для администратора. Владелец кампании определяется данными приложения, не ролью Owner. |
| exp / nbf | Срок действия; проверяются, clock skew не более 30 секунд |

Алгоритм локального профиля HS256. Проверять подпись, разрешённый алгоритм, issuer, audience, lifetime, обязательные claims и их однозначность. Настроить отображение claims/roles явно; не полагаться на случайное имя преобразованного claim. Валидный JWT не отменяет проверку активного профиля и актуальных grants. Service не притворяется Employee и не получает личный кошелёк. Дублирующиеся/противоречивые identity-claims отклонять.

Для тестов симметричный ключ доступен доверенному окружению; это НЕ модель распределения ключей промышленного IdP. Секрет не отдаётся пользователям, не попадает в Git, логи или отчёт сдачи. TLS нужен вне изолированного локального стенда. Ротация, refresh token, SSO и отзыв самого JWT вне объёма.

## Готовая обёртка
Нужны Python 3.10+ и выбранный .NET 10 SDK. Из каталога `agent`:

```bash
python auth/jwt.py prepare
```

Скрипт вызывает SDK-команду `dotnet user-jwts`, создаёт отдельный локальный служебный проект и сохраняет под `auth/.local/`:
- `api.env`: `Auth__Issuer`, `Auth__Audience`, `Auth__SigningKeyBase64`;
- `tokens/*.jwt`: employee123, employee124, employee125, admin1, progress-source, shop-source, employee123-other-company, admin1-other-company;
- `actors.json`: несекретные identity-данные для bootstrap.

Подключить `api.env` как env_file только к API; приложение должно валидировать ключ как декодированные Base64-байты, не UTF-8 символы Base64. Workers не выпускают токены. Bootstrap создаёт компании/профили/кампании/grants через application use cases; обёртка создаёт лишь токены, не бизнес-данные.

```bash
TOKEN=$(cat auth/.local/tokens/employee123.jwt)
curl -H "Authorization: Bearer $TOKEN" http://localhost:8080/<путь-из-OpenAPI>
```

Токены действуют 1 час; повтор `prepare` обновляет их, сохраняя ключ. Индивидуальный токен:

```bash
python auth/jwt.py token --kind user --master-id 123 --valid-for 1h
```

Результат этой команды — токен в stdout; не сохранять вывод в общий лог. Другая компания задаётся `--company`; сервис — `--kind service --subject shop-source`. Прямой эквивалент, без обёртки:

```bash
dotnet user-jwts create -p <служебный.csproj> --issuer motiva-benchmark \
  --audience motiva-api --name employee-123 --claim actorType=user \
  --claim companyId=11111111-1111-4111-8111-111111111111 \
  --claim masterId=123 --role Employee --valid-for 1h --output token
```

На сдаче проверить: нормальный Employee, Admin и Service; неверные подпись/issuer/audience, истёкший срок, отсутствующий masterId, подмена компании, заблокированный профиль, отозванный grant и replay после отзыва. Подготовка тестовых подписанных токенов разрешена в тестовом коде; authentication pipeline приложения не подменять.

Документация SDK: https://learn.microsoft.com/en-us/aspnet/core/security/authentication/jwt-authn?view=aspnetcore-10.0
Проверка JWT: https://learn.microsoft.com/en-us/aspnet/core/security/authentication/configure-jwt-bearer-authentication?view=aspnetcore-10.0

**Статус поставки:** синтаксис обёртки и 12 локальных тестов её вспомогательных функций проверены. В этой среде нет .NET SDK: реальный выпуск через `dotnet user-jwts` и подключение к API здесь не выполнялись. Это не подтверждённый runtime-стенд.
