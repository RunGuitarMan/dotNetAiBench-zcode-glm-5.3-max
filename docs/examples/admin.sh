#!/usr/bin/env bash
# Пример Admin: сотрудники, ресурсы, кампания до публикации, бюджет, гранты, аудит.
set -euo pipefail
cd "$(dirname "$0")/../.."
B="http://localhost:8080/api/v1"
ADMIN=$(cat auth/.local/tokens/admin1.jwt)
IDEM="example-$(date +%s)"

echo "== создать сотрудника (профиль + кошелёк одной транзакцией)"
curl -s -X POST -H "Authorization: Bearer $ADMIN" -H "Idempotency-Key: $IDEM-emp" -H "Content-Type: application/json" \
  -d '{"masterId":4242,"tags":["vip"]}' "$B/employees" -w "\nLocation: %{header_json}\n" | head -3

echo "== черновик кампании (виден только владельцу и админу)"
curl -s -X POST -H "Authorization: Bearer $ADMIN" -H "Idempotency-Key: $IDEM-camp" -H "Content-Type: application/json" \
  -d '{"code":"EX-A","name":"Example","ownerMasterId":1,"startsAt":"2026-10-01T00:00:00Z","endsAt":"2026-12-31T20:00:00Z"}' \
  "$B/campaigns" | python3 -c 'import json,sys;d=json.load(sys.stdin);print("campaignId:",d["id"],"status:",d["status"])'

echo "== журнал изменений прав/настроек/бюджета (B28.3)"
curl -s -H "Authorization: Bearer $ADMIN" "$B/audit-records?limit=5" | python3 -m json.tool | head -18

echo "== бюджет кампании (только владелец/админ)"
curl -s -H "Authorization: Bearer $ADMIN" "$B/campaigns/$1/budgets" | python3 -m json.tool
