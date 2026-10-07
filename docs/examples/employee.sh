#!/usr/bin/env bash
# Пример Employee (сотрудник): свои кампании, прогресс, кошелёк, траты, выписка.
set -euo pipefail
cd "$(dirname "$0")/../.."
B="http://localhost:8080/api/v1"
EMP=$(cat auth/.local/tokens/employee123.jwt)

echo "== доступные кампании текущего сезона"
curl -s -H "Authorization: Bearer $EMP" "$B/campaigns?limit=10" | python3 -m json.tool | head -20

echo "== свой кошелёк (все ресурсы компании, нулевые включены)"
curl -s -H "Authorization: Bearer $EMP" "$B/me/wallet" | python3 -m json.tool | head -14

echo "== своя история событий (после потери аудитории тоже доступна)"
curl -s -H "Authorization: Bearer $EMP" "$B/me/progress-events?limit=5" | python3 -m json.tool | head -24

echo "== трата со своего кошелька (1 единица)"
SYSTEM=$1; RESOURCE=$2
curl -s -X POST -H "Authorization: Bearer $EMP" -H "Content-Type: application/json" \
  -d "{\"purchaseSystemId\":\"$SYSTEM\",\"resourceId\":\"$RESOURCE\",\"amount\":1,\"operationNumber\":\"EXAMPLE-S-1\"}" \
  "$B/spends" | python3 -m json.tool

echo "== заказ выписки своих движений"
curl -s -X POST -H "Authorization: Bearer $EMP" -H "Idempotency-Key: example-export-1" -H "Content-Type: application/json" \
  -d '{"scope":"Own","fromUtc":"2026-01-01T00:00:00Z","toUtc":"2026-12-31T00:00:00Z"}' \
  "$B/exports" | python3 -m json.tool
