#!/usr/bin/env bash
# Пример Integration: доверенный источник передаёт прогресс, система покупок тратит и возвращает.
set -euo pipefail
cd "$(dirname "$0")/../.."
B="http://localhost:8080/api/v1"
SRC=$(cat auth/.local/tokens/progress-source.jwt)
SHOP=$(cat auth/.local/tokens/shop-source.jwt)
TASK=$1; MASTER=$2; SYSTEM=$3; RESOURCE=$4

echo "== событие прогресса (принято системой; creditedDelta = min-правило)"
curl -s -X POST -H "Authorization: Bearer $SRC" -H "Content-Type: application/json" \
  -d "{\"eventNumber\":\"EXAMPLE-E-1\",\"masterId\":$MASTER,\"taskId\":\"$TASK\",\"delta\":3}" \
  "$B/progress-events" | python3 -m json.tool

echo "== повтор того же номера — сохранённый ответ байт-в-байт (B16.2)"
curl -s -X POST -H "Authorization: Bearer $SRC" -H "Content-Type: application/json" \
  -d "{\"eventNumber\":\"EXAMPLE-E-1\",\"masterId\":$MASTER,\"taskId\":\"$TASK\",\"delta\":3}" \
  "$B/progress-events" | python3 -c 'import json,sys;d=json.load(sys.stdin);print("result:",d["result"],"credited:",d["creditedDelta"])'

echo "== сервисная трата указанному сотруднику"
curl -s -X POST -H "Authorization: Bearer $SHOP" -H "Content-Type: application/json" \
  -d "{\"masterId\":$MASTER,\"purchaseSystemId\":\"$SYSTEM\",\"resourceId\":\"$RESOURCE\",\"amount\":1,\"operationNumber\":\"EXAMPLE-SHOP-1\"}" \
  "$B/spends" | python3 -c 'import json,sys;d=json.load(sys.stdin);print("spend:",d["result"],d.get("refusalCode"))'

echo "== система читает свою историю трат/возвратов (только свои пары)"
curl -s -H "Authorization: Bearer $SHOP" "$B/me/operations?limit=5" | python3 -m json.tool | head -16
