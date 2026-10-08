#!/usr/bin/env bash
# T3-21: seeds the T09 profile and runs the k6 scenarios against the LB.
# Usage: scripts/load.sh seed|main|competitors|export100k|degradation|verify|all
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

# Samples docker stats into a CSV series — a resource PROFILE of the run, not one snapshot.
sample_stats() { # sample_stats OUTFILE DURATION_S
  local out=$1 duration=$2
  echo "timestamp,container,cpu,mem" > "$out"
  local end=$((SECONDS + duration))
  while [ "$SECONDS" -lt "$end" ]; do
    docker stats --no-stream --format "{{.Name}},{{.CPUPerc}},{{.MemUsage}}" 2>/dev/null \
      | grep -E "motiva-(api|worker|postgres|k6|valkey|s3)" | while IFS= read -r line; do
        echo "$(date +%H:%M:%S),$line" >> "$out"
      done
    sleep 10
  done
}

envs() { bash scripts/k6env.sh; }

main() {
  echo "== k6 main profile (100 RPS open model, 60s warmup + 300s, seeded PRNG, goodput gate)"
  date -u +%Y-%m-%dT%H:%M:%SZ > artifacts/load/main.started
  local args
  args=$(envs)
  sample_stats artifacts/load/stats-main.csv 380 &
  local stats_pid=$!
  # k6 prints banner and human summary to stdout with the JSON export appended last; the
  # JSON blob is extracted to its own file afterwards.
  # shellcheck disable=SC2086
  set +e
  docker compose run --rm $args k6 run --summary-export /dev/stdout /scripts/main.js \
    > >(tee artifacts/load/main.log) 2>&1
  local k6_exit=$?
  set -e
  python3 - <<'PY'
import json, pathlib
raw = pathlib.Path('artifacts/load/main.log').read_text()
start = raw.rfind('\n{')
try:
    obj, _ = json.JSONDecoder().raw_decode(raw[start + 1:])
    pathlib.Path('artifacts/load/main.summary.json').write_text(json.dumps(obj, indent=1))
except Exception as e:
    print('summary extraction failed:', e)
PY
  kill "$stats_pid" 2>/dev/null || true
  echo "== main summary (exit $k6_exit):"
  python3 - "artifacts/load/main.summary.json" <<'PY'
import json, sys
try:
    d = json.load(open(sys.argv[1]))
    m = d['metrics']
    def g(name, field='count'):
        return m.get(name, {}).get(field)
    http = g('http_reqs')
    iters = g('iterations')
    dropped = g('dropped_iterations') or 0
    print(f"planned≈33030 (ramp 1→100×60s + 100×300s); sent http_reqs={http}; completed iterations={iters}; dropped={dropped}")
    for name in ('motiva_goodput_rate', 'motiva_events_accepted', 'motiva_spends_posted', 'motiva_failed_requests', 'checks'):
        if name in m:
            print(name, '=', m[name].get('count', m[name].get('rate')))
except Exception as e:
    print('summary parse (see raw file):', e)
PY
  [ "$k6_exit" -eq 0 ] || exit "$k6_exit"
}

competitors() {
  echo "== k6 two-competitor scenario (aggregated Counter thresholds — the run FAILS on a broken invariant)"
  local args
  args=$(K6_REQUIRE_RACE=1 bash scripts/k6env.sh)
  # shellcheck disable=SC2086
  docker compose run --rm $args k6 run /scripts/competitors.js 2>&1 | tee artifacts/load/competitors.log | tail -12
  python3 scripts/load_verify.py
}

export100k() {
  echo "== export 100k rows within 120s with content verification (T07)"
  python3 scripts/load_export100k.py 2>&1 | tee artifacts/load/export100k.log
}

# degradation() runs the SAME profile (100 RPS, 50/25/10/10/5 via the shared mix.js) while
# dependencies fail (T09, D15): Valkey 30 s, S3 30 s, one API 10 s.
degradation() {
  echo "== degradation pass: SAME 100 RPS mix + Valkey 30s / S3 30s / one API 10s"
  local args
  args=$(envs)
  ( sleep 90; docker pause motiva-valkey-1 >/dev/null; sleep 30; docker unpause motiva-valkey-1 >/dev/null ) &
  local valkey_pid=$!
  ( sleep 150; docker stop motiva-s3-1 >/dev/null 2>&1; sleep 30; docker start motiva-s3-1 >/dev/null 2>&1 ) &
  local s3_pid=$!
  ( sleep 210; docker stop motiva-api-2 >/dev/null 2>&1; sleep 10; docker start motiva-api-2 >/dev/null 2>&1 ) &
  local api_pid=$!
  sample_stats artifacts/load/stats-degradation.csv 380 &
  local stats_pid=$!
  # Same extraction as the main profile; no hard thresholds in this pass (D15): it documents
  # the degradation profile while the same mix keeps running.
  # shellcheck disable=SC2086
  docker compose run --rm $args k6 run --summary-export /dev/stdout /scripts/degradation.js \
    > >(tee artifacts/load/degradation.log) 2>&1
  local k6_exit=$?
  python3 - <<'PY'
import json, pathlib
raw = pathlib.Path('artifacts/load/degradation.log').read_text()
start = raw.rfind('\n{')
try:
    obj, _ = json.JSONDecoder().raw_decode(raw[start + 1:])
    pathlib.Path('artifacts/load/degradation.summary.json').write_text(json.dumps(obj, indent=1))
except Exception as e:
    print('degradation summary extraction failed:', e)
PY
  kill "$stats_pid" 2>/dev/null || true
  wait "$valkey_pid" "$s3_pid" "$api_pid" 2>/dev/null || true
  echo "degradation pass complete (exit $k6_exit; see artifacts/load/degradation.{summary.json,log} + stats-degradation.csv)"
}

verify() {
  python3 scripts/load_verify.py
}

case "$MODE" in
  seed) seed ;;
  main) main ;;
  competitors) competitors ;;
  export100k) export100k ;;
  degradation) degradation ;;
  verify) verify ;;
  all) seed; main; competitors; export100k; degradation; verify ;;
  *) echo "usage: $0 seed|main|competitors|export100k|degradation|verify|all"; exit 2 ;;
esac
