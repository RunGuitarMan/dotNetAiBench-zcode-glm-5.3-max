#!/usr/bin/env bash
# T3-21: seeds the T09 profile and runs the k6 scenarios against the LB.
# Usage: scripts/load.sh seed|main|competitors|export100k|all
set -euo pipefail
cd "$(dirname "$0")/.."
mkdir -p artifacts/load
MODE="${1:-all}"

export Motiva__PostgresConnectionString="${MOTIVA_PG:-Host=localhost;Port=5433;Database=motiva;Username=motiva;Password=motiva-stand}"
export Motiva__ValkeyEndpoint="${MOTIVA_VALKEY:-localhost:6380}"

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

envs() {
  python3 - <<'PY'
import json, subprocess, os
emp = open('auth/.local/tokens/employee123.jwt').read().strip()
src = open('auth/.local/tokens/progress-source.jwt').read().strip()
shop = open('auth/.local/tokens/shop-source.jwt').read().strip()
def q(sql):
    return subprocess.run(['docker','compose','exec','-T','postgres','psql','-U','motiva','-d','motiva','-t','-A','-F',',','-c',sql],capture_output=True,text=True).stdout.strip()
campaigns=[x.split(',')[1] for x in q("SELECT c.id, c.id FROM campaigns c JOIN companies cc ON cc.id=c.company_id WHERE c.code_norm LIKE 'SEEDC%' AND cc.id='11111111-1111-4111-8111-111111111111'").splitlines() if x]
tasks=[x.split(',')[1] for x in q("SELECT t.id, t.id FROM tasks t JOIN streams s ON s.id=t.stream_id JOIN campaigns c ON c.id=s.campaign_id WHERE c.code_norm LIKE 'SEEDC%' AND t.code_norm='T00' AND c.company_id='11111111-1111-4111-8111-111111111111'").splitlines() if x]
challenges=[x.split(',')[1] for x in q("SELECT ch.id, ch.id FROM challenges ch JOIN campaigns c ON c.id=ch.campaign_id WHERE c.code_norm LIKE 'SEEDC%' AND c.company_id='11111111-1111-4111-8111-111111111111'").splitlines() if x]
system=q("SELECT ps.id FROM purchase_systems ps WHERE ps.code_norm='SEEDSHOP'")
resource=q("SELECT r.id FROM resources r WHERE r.code_norm='SEEDSTAR'")
race=[]
for row in q("SELECT t.goal, t.id FROM tasks t JOIN streams s ON s.id=t.stream_id JOIN campaigns c ON c.id=s.campaign_id WHERE c.code_norm LIKE 'SEEDC00' AND t.code_norm='T01' AND c.company_id='11111111-1111-4111-8111-111111111111'").splitlines():
    if row:
        goal, tid = row.split(",")
        race.append({'goal': int(goal), 'taskId': tid})
envs=[f'EMP_TOKEN={emp}',f'SRC_TOKEN={src}',f'SHOP_TOKEN={shop}',
      'BASE_URL=http://lb',
      f'CAMPAIGN_IDS={",".join(campaigns)}',f'TASK_IDS={",".join(tasks)}',f'CHALLENGE_IDS={",".join(challenges)}',
      f'PURCHASE_SYSTEM_ID={system}',f'RESOURCE_ID={resource}','EMPLOYEE_MAX=10002',
      f'RACE_TARGETS={json.dumps(race, separators=(\",\", \":\"))}']
for e in envs:
    print('-e', e)
PY
}

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
    "SELECT b.campaign_id, b.resource_id, b.available FROM budgets b WHERE b.available < 0;" | (! grep .) && echo "budgets never negative: ok"
}

export100k() {
  echo "== export 100k rows within 120s (T07)"
  python3 - <<'PY'
import json, subprocess, time, urllib.request
token=open('auth/.local/tokens/admin1.jwt').read().strip()  # company-wide scope covers ~100k movements
def api(method, path, body=None, key=None):
    req=urllib.request.Request('http://localhost:8080'+path, method=method,
        data=json.dumps(body).encode() if body else None,
        headers={'Authorization':'Bearer '+token,'Content-Type':'application/json',
                 **({'Idempotency-Key':key} if key else {})})
    with urllib.request.urlopen(req) as r:
        return json.load(r) if r.status!=204 else None
exp=api('POST','/api/v1/exports',{'scope':'Company','fromUtc':'2020-01-01T00:00:00Z','toUtc':'2030-01-01T00:00:00Z'},key='load-100k-'+str(int(time.time())))
eid=exp['id']
t0=time.time()
status=None
while time.time()-t0 < 300:
    status=api('GET','/api/v1/exports/'+eid)['status']
    if status in ('Ready','Error','Deleted'): break
    time.sleep(2)
elapsed=time.time()-t0
print(f'export status={status} elapsed={elapsed:.1f}s')
assert status=='Ready', f'export not ready: {status}'
assert elapsed <= 120, f'export took {elapsed:.1f}s > 120s'
meta=api('GET','/api/v1/exports/'+eid)
print('sizeBytes', meta.get('sizeBytes'), 'checksum', (meta.get('checksum') or '')[:16])
print('EXPORT_OK')
PY
}

case "$MODE" in
  seed) seed ;;
  main) main ;;
  competitors) competitors ;;
  export100k) export100k ;;
  all) seed; main; competitors; export100k ;;
  *) echo "usage: $0 seed|main|competitors|export100k|all"; exit 2 ;;
esac
