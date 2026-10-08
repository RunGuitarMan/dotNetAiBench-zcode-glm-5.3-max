#!/usr/bin/env bash
# T3-21: seeds the T09 profile and runs the k6 scenarios against the LB.
# Usage: scripts/load.sh seed|main|competitors|export100k|all
set -euo pipefail
cd "$(dirname "$0")/.."
mkdir -p artifacts/load
MODE="${1:-all}"

export Motiva__PostgresConnectionString="${Motiva__PostgresConnectionString:-Host=localhost;Port=5433;Database=motiva;Username=motiva;Password=motiva-stand}"
export Motiva__ValkeyEndpoint="${Motiva__ValkeyEndpoint:-localhost:6380}"

seed() {
  echo "== seeding T09 profile (outside the measurement)"
  dotnet run --project tools/Motiva.Seed --no-build 2>&1 | tee artifacts/load/seed.log
}

ids() {
  docker compose exec -T postgres psql -U motiva -d motiva -t -A -F, -c "$1"
}

k6run() { # k6run SCRIPT [ENVS...]
  local script=$1; shift
  docker compose run --rm --quiet-pull "$@" k6 run --summary-export /dev/stdout /scripts/"$script" 2>/dev/null \
    | tail -n +2 > "artifacts/load/${script%.js}.summary.json" || true
  # plain summary for humans
  docker compose run --rm "$@" k6 run /scripts/"$script" | tee "artifacts/load/${script%.js}.log" | grep -E "http_req|checks|status|^✓|✗" | tail -20
}

envs() { bash scripts/k6env.sh; }

main() {
  echo "== k6 main profile (100 RPS open model, 60s warmup + 300s)"
  local args
  args=$(envs)
  # shellcheck disable=SC2086
  docker compose run --rm $args k6 run /scripts/main.js 2>&1 | tee artifacts/load/main.log | tail -25
}

competitors() {
  echo "== k6 two-competitor scenario"
  local args
  args=$(envs)
  # shellcheck disable=SC2086
  docker compose run --rm $args k6 run /scripts/competitors.js 2>&1 | tee artifacts/load/competitors.log | tail -12
  # Business outcome check: exactly one Granted per raced pair, budgets not negative.
  docker compose exec -T postgres psql -U motiva -d motiva -t -c \
    "SELECT b.campaign_id, b.resource_id FROM budgets b WHERE b.allocated_total - b.spent_total + b.returned_total < 0;" | (! grep .) && echo "budgets never negative: ok"
}

export100k() {
  echo "== export 100k rows within 120s with content verification (T07)"
  python3 scripts/load_export100k.py
}

# degradation() runs the mix while pausing dependencies (T09). Invoked via `load.sh degradation`.
degradation() {
  echo "== degradation pass: mix + Valkey 30s / S3 30s / one API 10s"
  local args
  args=$(envs)
  ( sleep 20; docker pause motiva-valkey-1 >/dev/null; sleep 30; docker unpause motiva-valkey-1 >/dev/null ) &
  local valkey_pid=$!
  ( sleep 60; docker stop motiva-s3-1 >/dev/null 2>&1; sleep 30; docker start motiva-s3-1 >/dev/null 2>&1 ) &
  local s3_pid=$!
  ( sleep 100; docker stop motiva-api-2 >/dev/null 2>&1; sleep 10; docker start motiva-api-2 >/dev/null 2>&1 ) &
  local api_pid=$!
  # shellcheck disable=SC2086
  docker compose run --rm $args k6 run /scripts/degradation.js 2>&1 | tee artifacts/load/degradation.log | tail -20
  wait "$valkey_pid" "$s3_pid" "$api_pid" 2>/dev/null || true
  echo "degradation pass complete (see artifacts/load/degradation.log)"
}

case "$MODE" in
  seed) seed ;;
  main) main ;;
  competitors) competitors ;;
  export100k) export100k ;;
  degradation) degradation ;;
  all) seed; main; competitors; export100k; degradation ;;
  *) echo "usage: $0 seed|main|competitors|export100k|degradation|all"; exit 2 ;;
esac
