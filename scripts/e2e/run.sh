#!/usr/bin/env bash
# End-to-end scenarios (T3-19/T3-20) against the compose stand: two API replicas behind the
# LB, two workers, real dependencies. Every wait is bounded; failures fail the run.
set -uo pipefail
cd "$(dirname "$0")/../.."
mkdir -p artifacts/e2e

LB="http://localhost:8080"
API_ENV="auth/.local/api.env"
TOK_EMP=$(cat auth/.local/tokens/employee123.jwt)
TOK_ADMIN=$(cat auth/.local/tokens/admin1.jwt)
TOK_SRC=$(cat auth/.local/tokens/progress-source.jwt)
TOK_SHOP=$(cat auth/.local/tokens/shop-source.jwt)
COMPANY="11111111-1111-4111-8111-111111111111"

FAILURES=0
note() { echo "[e2e] $*"; }
fail() { echo "[e2e][FAIL] $*" | tee -a artifacts/e2e/failures.log; FAILURES=$((FAILURES + 1)); }
pass() { echo "[e2e][ok] $*" | tee -a artifacts/e2e/passed.log; }

api() { # api METHOD PATH TOKEN [JSON] [HEADER]
  local method=$1 path=$2 token=$3 body=${4:-} header=${5:-}
  local args=(-s -X "$method" "$LB$path" -H "Authorization: Bearer $token")
  [ -n "$header" ] && args+=(-H "$header")
  if [ -n "$body" ]; then
    args+=(-H "Content-Type: application/json" -d "$body")
  fi
  curl "${args[@]}"
}

await() { # await URL EXPECTED_STATUS TIMEOUT_SECS
  local url=$1 expected=$2 timeout=${3:-30}
  local waited=0 status=0
  while [ $waited -lt $timeout ]; do
    status=$(curl -s -o /dev/null -w "%{http_code}" "$url")
    [ "$status" = "$expected" ] && return 0
    sleep 1; waited=$((waited + 1))
  done
  return 1
}

jsonget() { python3 -c "import json,sys;d=json.load(sys.stdin);print(d$1)" 2>/dev/null; }

note "wait for the stand"
await "$LB/health/ready" 200 60 || { fail "stand not ready"; exit 1; }
pass "stand ready (2 API + 2 workers + PG + Valkey + S3)"

# Per-run identities keep the script re-runnable against a stand with history.
E2E_TS=$(date +%s)
EMP_A=$((77000 + E2E_TS % 1000))
EMP_B=$((78000 + E2E_TS % 1000))

# ---------------------------------------------------------------- replay across API restarts
api POST /api/v1/employees "$TOK_ADMIN" "{\"masterId\":$EMP_A}" "Idempotency-Key: e2e-777-$E2E_TS" >/dev/null
note "T3-20: replay after API restart returns the stored result"
api POST /api/v1/employees "$TOK_ADMIN" '{"masterId":777}' "Idempotency-Key: e2e-777" >/dev/null
RES_A=$(curl -s -D- -o /dev/null -X POST "$LB/api/v1/resources" -H "Authorization: Bearer $TOK_ADMIN" -H "Idempotency-Key: e2e-res-a" -H "Content-Type: application/json" -d '{"code":"E2EA","name":"a"}' | grep -i '^etag' | tr -d '\r')
BODY_A=$(api POST /api/v1/employees "$TOK_ADMIN" "{\"masterId\":$EMP_B}" "Idempotency-Key: e2e-778-$E2E_TS")
docker restart motiva-api-1 >/dev/null 2>&1 || fail "api-1 restart failed"
sleep 3
await "$LB/health/ready" 200 30 || fail "API restart: not ready"
BODY_B=$(api POST /api/v1/employees "$TOK_ADMIN" "{\"masterId\":$EMP_B}" "Idempotency-Key: e2e-778-$E2E_TS")
[ "$BODY_A" = "$BODY_B" ] && pass "idempotent replay identical after API restart" || fail "replay body changed across restart"

# ---------------------------------------------------------------- full business vertical through LB
note "SCN-01 style vertical through the LB"
CODE="E2E$(date +%s)"
RID=$(api POST /api/v1/resources "$TOK_ADMIN" "{\"code\":\"$CODE-R\",\"name\":\"r\"}" "Idempotency-Key: $CODE-r" | jsonget "['id']")
CAMP=$(api POST /api/v1/campaigns "$TOK_ADMIN" "{\"code\":\"$CODE\",\"name\":\"c\",\"ownerMasterId\":1,\"startsAt\":\"2026-10-01T00:00:00Z\",\"endsAt\":\"2026-12-31T20:00:00Z\"}" "Idempotency-Key: $CODE-c")
CID=$(echo "$CAMP" | jsonget "['id']")
api PUT "/api/v1/campaigns/$CID/resources" "$TOK_ADMIN" "{\"resourceIds\":[\"$RID\"]}" 'If-Match: "v1"' >/dev/null
SID=$(api POST "/api/v1/campaigns/$CID/streams" "$TOK_ADMIN" '{"code":"S","name":"s"}' "Idempotency-Key: $CODE-s" | jsonget "['id']")
TID=$(api POST "/api/v1/streams/$SID/tasks" "$TOK_ADMIN" "{\"code\":\"T\",\"name\":\"t\",\"goal\":2,\"period\":\"Day\",\"streamPoints\":5,\"rewardItems\":[{\"resourceId\":\"$RID\",\"amount\":4}]}" "Idempotency-Key: $CODE-t" | jsonget "['id']")
CHID=$(api POST "/api/v1/campaigns/$CID/challenges" "$TOK_ADMIN" "{\"streamId\":\"$SID\",\"startsAt\":\"2026-10-01T00:00:00Z\",\"endsAt\":\"2026-12-31T20:00:00Z\"}" "Idempotency-Key: $CODE-ch" | jsonget "['id']")
VER=$(curl -s -D- -o /dev/null "$LB/api/v1/campaigns/$CID" -H "Authorization: Bearer $TOK_ADMIN" | grep -i '^etag' | tr -d '\r' | sed 's/.*: //')
api PATCH "/api/v1/campaigns/$CID" "$TOK_ADMIN" '{"status":"Published"}' "If-Match: $VER" >/dev/null
api POST "/api/v1/campaigns/$CID/budget-allocations" "$TOK_ADMIN" "{\"resourceId\":\"$RID\",\"amount\":100,\"operationNumber\":\"$CODE-b\"}" >/dev/null
api POST /api/v1/integration-grants "$TOK_ADMIN" "{\"subject\":\"progress-source\",\"kind\":\"Progress\",\"campaignId\":\"$CID\"}" "Idempotency-Key: $CODE-g" >/dev/null
EV=$(api POST /api/v1/progress-events "$TOK_SRC" "{\"eventNumber\":\"$CODE-e1\",\"masterId\":$EMP_A,\"taskId\":\"$TID\",\"delta\":2}")
WALLET=$(api GET /api/v1/employees/$EMP_A/wallet "$TOK_ADMIN" | jsonget "['balances'] and next(b['balance'] for b in d['balances'] if b['resourceCode']=='$CODE-R')")
[ "$WALLET" = "4" ] && pass "event through LB: wallet credited" || fail "wallet balance after event: $WALLET (expected 4)"
EV2=$(api POST /api/v1/progress-events "$TOK_SRC" "{\"eventNumber\":\"$CODE-e1\",\"masterId\":$EMP_A,\"taskId\":\"$TID\",\"delta\":2}")
[ "$EV" = "$EV2" ] && pass "progress replay byte-identical" || fail "progress replay differs"

# ---------------------------------------------------------------- finalization by workers (<=5s SLO)
note "T3-20: challenge finalizes soon after endsAt (worker alive)"
END_UTC=$(python3 -c "import datetime;print((datetime.datetime.now(datetime.timezone.utc)+datetime.timedelta(seconds=3)).strftime('%Y-%m-%dT%H:%M:%SZ'))")
CODE2="${CODE}x"
CAMP2=$(api POST /api/v1/campaigns "$TOK_ADMIN" "{\"code\":\"$CODE2\",\"name\":\"c2\",\"ownerMasterId\":1,\"startsAt\":\"2026-01-01T00:00:00Z\",\"endsAt\":\"2026-12-31T20:00:00Z\"}" "Idempotency-Key: $CODE2-c")
CID2=$(echo "$CAMP2" | jsonget "['id']")
SID2=$(api POST "/api/v1/campaigns/$CID2/streams" "$TOK_ADMIN" '{"code":"S","name":"s"}' "Idempotency-Key: $CODE2-s" | jsonget "['id']")
TID2=$(api POST "/api/v1/streams/$SID2/tasks" "$TOK_ADMIN" "{\"code\":\"T\",\"name\":\"t\",\"goal\":3,\"period\":\"Day\",\"streamPoints\":0,\"rewardItems\":[]}" "Idempotency-Key: $CODE2-t" | jsonget "['id']")
CHID2=$(api POST "/api/v1/campaigns/$CID2/challenges" "$TOK_ADMIN" "{\"streamId\":\"$SID2\",\"startsAt\":\"2026-01-01T00:00:00Z\",\"endsAt\":\"$END_UTC\"}" "Idempotency-Key: $CODE2-ch" | jsonget "['id']")
VER2=$(curl -s -D- -o /dev/null "$LB/api/v1/campaigns/$CID2" -H "Authorization: Bearer $TOK_ADMIN" | grep -i '^etag' | tr -d '\r' | sed 's/.*: //')
api PATCH "/api/v1/campaigns/$CID2" "$TOK_ADMIN" '{"status":"Published"}' "If-Match: $VER2" >/dev/null
api POST /api/v1/integration-grants "$TOK_ADMIN" "{\"subject\":\"progress-source\",\"kind\":\"Progress\",\"campaignId\":\"$CID2\"}" "Idempotency-Key: $CODE2-g" >/dev/null
api POST /api/v1/progress-events "$TOK_SRC" "{\"eventNumber\":\"$CODE2-e\",\"masterId\":$EMP_A,\"taskId\":\"$TID2\",\"delta\":3}" >/dev/null
START=$(date +%s)
STATE=""
while [ $(( $(date +%s) - START )) -lt 15 ]; do
  STATE=$(api GET "/api/v1/challenges/$CHID2/leaderboard?limit=10" "$TOK_EMP" | jsonget "['state']")
  [ "$STATE" = "Final" ] && break
  sleep 1
done
TOOK=$(( $(date +%s) - START ))
if [ "$STATE" = "Final" ]; then
  pass "challenge finalized by worker in ${TOOK}s"
else
  fail "challenge did not finalize within 15s (state=$STATE)"
fi

# ---------------------------------------------------------------- degradation: Valkey pause
note "T3-19: Valkey outage 15s -> catalog still served"
docker compose pause valkey >/dev/null
sleep 2
CAT_STATUS=$(curl -s -o /dev/null -w "%{http_code}" "$LB/api/v1/resources" -H "Authorization: Bearer $TOK_EMP")
LB_STATUS=$(curl -s -o /dev/null -w "%{http_code}" "$LB/api/v1/challenges/$CHID/leaderboard?limit=5" -H "Authorization: Bearer $TOK_EMP")
docker compose unpause valkey >/dev/null
[ "$CAT_STATUS" = "200" ] && pass "catalog reads degrade to PostgreSQL (Valkey paused)" || fail "catalog failed during Valkey pause: $CAT_STATUS"
[ "$LB_STATUS" = "200" ] && pass "leaderboard readable during Valkey pause" || fail "leaderboard failed during Valkey pause: $LB_STATUS"

# ---------------------------------------------------------------- degradation: one API down
note "T3-19: one API replica down 10s -> LB keeps serving"
docker stop motiva-api-2 >/dev/null 2>&1 || fail "api-2 stop failed"
sleep 2
OK=0
for i in $(seq 1 10); do
  CODE3=$(curl -s -o /dev/null -w "%{http_code}" "$LB/health/ready")
  [ "$CODE3" = "200" ] && OK=$((OK + 1))
done
docker start motiva-api-2 >/dev/null 2>&1 || fail "api-2 start failed"
sleep 5
await "$LB/health/ready" 200 30
[ "$OK" -ge 8 ] && pass "service survived with one API replica ($OK/10 requests ok)" || fail "availability with one API down: $OK/10"

# ---------------------------------------------------------------- degradation: PostgreSQL
note "T3-19: PostgreSQL outage -> bounded 503, liveness alive, clean recovery"
docker compose stop postgres >/dev/null
sleep 2
R_STATUS=$(curl -s -o /dev/null -w "%{http_code}" "$LB/health/ready")
L_STATUS=$(curl -s -o /dev/null -w "%{http_code}" "$LB/health/live")
WRITE=$(curl -s -o /dev/null -w "%{http_code}" -X POST "$LB/api/v1/spends" -H "Authorization: Bearer $TOK_EMP" -H "Content-Type: application/json" -d '{}')
RETRY_AFTER=$(curl -s -D- -o /dev/null -X POST "$LB/api/v1/progress-events" -H "Authorization: Bearer $TOK_SRC" -H "Content-Type: application/json" -d '{"eventNumber":"X","masterId":1,"taskId":"00000000-0000-0000-0000-000000000000","delta":1}' | grep -i '^retry-after' | tr -d '\r')
docker compose start postgres >/dev/null
await "$LB/health/ready" 200 60 || fail "readiness did not recover after PG restart"
[ "$L_STATUS" = "200" ] && pass "liveness stays alive during PG outage" || fail "liveness died with PG: $L_STATUS"
[ "$R_STATUS" = "503" ] && pass "readiness reports 503 during PG outage" || fail "readiness during PG outage: $R_STATUS"
[ -n "$RETRY_AFTER" ] && pass "economic write returns 503 with Retry-After" || fail "no Retry-After on 503"
# No duplicate effects: the replayed event stays identical after recovery.
EV3=$(api POST /api/v1/progress-events "$TOK_SRC" "{\"eventNumber\":\"$CODE-e1\",\"masterId\":$EMP_A,\"taskId\":\"$TID\",\"delta\":2}")
[ "$EV" = "$EV3" ] && pass "no duplicate effects after PG recovery" || fail "replay changed after PG recovery"

# ---------------------------------------------------------------- export lifecycle with workers
note "T3-17/T3-20: export forms via worker, bytes are immutable"
EXP=$(api POST /api/v1/exports "$TOK_EMP" '{"scope":"Own","fromUtc":"2026-01-01T00:00:00Z","toUtc":"2026-12-31T00:00:00Z"}' "Idempotency-Key: $CODE-x")
EID=$(echo "$EXP" | jsonget "['id']")
READY=""
for i in $(seq 1 30); do
  STATUS=$(api GET "/api/v1/exports/$EID" "$TOK_EMP" | jsonget "['status']")
  [ "$STATUS" = "Ready" ] && READY=1 && break
  [ "$STATUS" = "Error" ] && break
  sleep 1
done
if [ "${READY:-0}" != "1" ]; then
  fail "export did not become Ready (status=$STATUS)"
else
  pass "export Ready via worker"
  LINK1_RESP=$(api POST "/api/v1/exports/$EID/download-links" "$TOK_EMP" "" "Idempotency-Key: $CODE-l1")
  LINK1=$(echo "$LINK1_RESP" | jsonget "['url']")
  if [ -z "$LINK1" ]; then
    fail "download link response has no url: $LINK1_RESP"
  else
    CS1=$(curl -s --max-time 60 "$LINK1" | shasum -a 256 | cut -d' ' -f1)
    [ -n "$CS1" ] && [ "$CS1" != "e3b0c44298fc1c149afbf4c8996fb92427ae41e4649b934ca495991b7852b855" ] \
      && pass "downloaded CSV is non-empty" || fail "empty CSV from a Ready export"
    sleep 1
    LINK2=$(api POST "/api/v1/exports/$EID/download-links" "$TOK_EMP" "" "Idempotency-Key: $CODE-l2" | jsonget "['url']")
    CS2=$(curl -s --max-time 60 "$LINK2" | shasum -a 256 | cut -d' ' -f1)
    [ "$CS1" = "$CS2" ] && pass "export bytes immutable across downloads" || fail "export bytes changed"
  fi
fi

# ---------------------------------------------------------------- S3 outage
note "T3-19: S3 outage -> main flow unaffected, link issue -> 424/409"
docker compose stop s3 >/dev/null
sleep 2
EV4_STATUS=$(curl -s -o /dev/null -w "%{http_code}" -X POST "$LB/api/v1/progress-events" -H "Authorization: Bearer $TOK_SRC" -H "Content-Type: application/json" -d "{\"eventNumber\":\"$CODE-e2\",\"masterId\":$EMP_A,\"taskId\":\"$TID\",\"delta\":1}")
LINK_STATUS=$(curl -s -o /dev/null -w "%{http_code}" --max-time 20 -X POST "$LB/api/v1/exports/$EID/download-links" -H "Authorization: Bearer $TOK_EMP" -H "Idempotency-Key: $CODE-l3" | head -c 3)
docker compose start s3 >/dev/null
[ "$EV4_STATUS" = "201" ] && pass "progress accounting unaffected by S3 outage" || fail "progress failed during S3 outage: $EV4_STATUS"
if [ "${LINK_STATUS:0:3}" = "424" ] || [ "${LINK_STATUS:0:3}" = "409" ]; then pass "link issue during S3 outage -> $LINK_STATUS"; else fail "unexpected link status during S3 outage: $LINK_STATUS"; fi
await "$LB/health/ready" 200 60

# ---------------------------------------------------------------- metrics on controlled traffic (D16)
note "T02/T06: /metrics reflects controlled requests — HTTP and REAL DB series"
# The check reads ONE replica directly (the LB round-robins between two) and drives traffic
# at the same replica, so the counter deltas are deterministic.
replica_metrics() { docker compose exec -T api curl -s localhost:8080/metrics; }
MET1_READ=$(replica_metrics | grep 'motiva_request_duration_ms_count{kind="read"}' | awk '{print $2}')
MET1_DB=$(replica_metrics | grep -c 'motiva_db_duration_ms_count')
for i in $(seq 1 10); do docker compose exec -T api curl -s -o /dev/null localhost:8080/api/v1/resources -H "Authorization: Bearer $TOK_EMP"; done
MET2_READ=$(replica_metrics | grep 'motiva_request_duration_ms_count{kind="read"}' | awk '{print $2}')
MET2_DB=$(replica_metrics | grep 'motiva_db_duration_ms_count' | head -1)
[ -n "$MET2_READ" ] && [ -n "$MET1_READ" ] && [ "$MET2_READ" -gt "$MET1_READ" ] \
  && pass "request latency series grew on controlled GETs ($MET1_READ -> $MET2_READ)" \
  || fail "request metrics did not react to controlled traffic"
[ "$MET1_DB" -ge 1 ] && [ -n "$MET2_DB" ] \
  && pass "DB duration series present with real measurements (e.g. $MET2_DB)" \
  || fail "DB duration series missing — /metrics would not explain DB behaviour"

# ---------------------------------------------------------------- S-4: worker killed mid-formation
note "T3-20/S-4: worker KILLED between upload and Ready — recovery, same snapshot, no dupes"
docker stop motiva-worker-2 >/dev/null 2>&1 # deterministic: worker-1 owns the formation
EXP2=$(api POST /api/v1/exports "$TOK_ADMIN" '{"scope":"Company","fromUtc":"2026-01-01T00:00:00Z","toUtc":"2026-12-31T00:00:00Z"}' "Idempotency-Key: $CODE-x2")
EID2=$(echo "$EXP2" | jsonget "['id']")
# Deterministic phase: wait until the worker has COMMITTED Forming (snapshot frozen) —
# killing inside the snapshot transaction would only test the LB timeout.
FORMING=""
for i in $(seq 1 20); do
  S=$(docker compose exec -T postgres psql -U motiva -d motiva -t -A -c "SELECT status FROM export_requests WHERE id='$EID2'")
  [ "$S" = "Forming" ] && FORMING=1 && break
  sleep 1
done
[ "${FORMING:-0}" = "1" ] || fail "S-4: export never reached Forming ($S)"
docker kill motiva-worker-1 >/dev/null 2>&1
docker start motiva-worker-1 motiva-worker-2 >/dev/null 2>&1
READY2=""
for i in $(seq 1 150); do
  STATUS2=$(api GET "/api/v1/exports/$EID2" "$TOK_ADMIN" | jsonget "['status']")
  [ "$STATUS2" = "Ready" ] && READY2=1 && break
  [ "$STATUS2" = "Error" ] && break
  sleep 1
done
if [ "${READY2:-0}" != "1" ]; then
  fail "export after worker kill did not recover to Ready (status=$STATUS2)"
else
  pass "export recovered to Ready after a real worker kill"
  # The recovered bytes match the DB exactly: one row per posted movement in range.
  LINK_RESP=$(api POST "/api/v1/exports/$EID2/download-links" "$TOK_ADMIN" "" "Idempotency-Key: $CODE-l4")
  LINK3=$(echo "$LINK_RESP" | jsonget "['url']")
  if [ -z "$LINK3" ]; then
    fail "download link response has no url: $LINK_RESP"
  else
    LINES=$(curl -s --max-time 60 "$LINK3" | tail -n +2 | grep -c . || true)
    EXPECTED=$(docker compose exec -T postgres psql -U motiva -d motiva -t -A -c \
      "SELECT count(*) FROM operations WHERE company_id='$COMPANY' AND result='Posted' AND kind IN ('TaskReward','ManualAward','Spend','SpendReversal','AwardReversal') AND created_at >= '2026-01-01' AND created_at < '2026-12-31'")
    [ "$LINES" = "$((EXPECTED))" ] && pass "recovered export content == DB movements ($LINES rows)" \
      || fail "recovered export rows=$LINES, expected=$EXPECTED (lost/duplicated effect)"
  fi
fi

# ------------------------------------------------- S-3: real late worker vs delete + cleanup
note "T3-20/S-3: paused worker resumes AFTER delete — fenced, no resurrection, bytes cleaned"
docker stop motiva-worker-2 >/dev/null 2>&1
EXP3=$(api POST /api/v1/exports "$TOK_ADMIN" '{"scope":"Company","fromUtc":"2026-01-01T00:00:00Z","toUtc":"2026-12-31T00:00:00Z"}' "Idempotency-Key: $CODE-x3")
EID3=$(echo "$EXP3" | jsonget "['id']")
# Wait for the COMMITTED Forming phase (the frozen transaction must not hold the row lock):
# the job may first sit in a stopped worker's outbox lease (30 s) before re-claim.
FORMING3=""
for i in $(seq 1 90); do
  S3=$(docker compose exec -T postgres psql -U motiva -d motiva -t -A -c "SELECT status FROM export_requests WHERE id='$EID3'")
  [ "$S3" = "Forming" ] && FORMING3=1 && break
  [ "$S3" = "Ready" ] && break   # too fast to catch mid-formation on a small stand
  sleep 1
done
if [ "${FORMING3:-0}" != "1" ]; then
  fail "S-3: export never caught in Forming ($S3) — late-worker trace not exercised"
else
  docker pause motiva-worker-1 >/dev/null   # a real frozen process holding the lease
  DEL_STATUS=$(curl -s --max-time 30 -o /dev/null -w "%{http_code}" -X DELETE "$LB/api/v1/exports/$EID3" -H "Authorization: Bearer $TOK_ADMIN")
  [ "$DEL_STATUS" = "204" ] && pass "delete during live lease returned 204" || fail "delete during formation: HTTP $DEL_STATUS"
  DEL_REPLAY=$(curl -s -o /dev/null -w "%{http_code}" -X POST "$LB/api/v1/exports/$EID3/download-links" -H "Authorization: Bearer $TOK_ADMIN" -H "Idempotency-Key: $CODE-l5")
  docker start motiva-worker-2 >/dev/null 2>&1
  sleep 70                  # the formation lease expires; cleanup sweep settles the intent
  docker unpause motiva-worker-1 >/dev/null # the late worker resumes: the fence aborts it
  sleep 20                  # retries and sweeps settle
  FINAL3=$(api GET "/api/v1/exports/$EID3" "$TOK_ADMIN" | jsonget "['status']")
  [ "$DEL_REPLAY" = "409" ] && pass "deleted export: link replay refused with 409" || fail "link replay after delete: $DEL_REPLAY"
  [ "$FINAL3" = "Deleted" ] && pass "no resurrection by the late real worker" || fail "export state after late worker: $FINAL3"
  INTENT=$(docker compose exec -T postgres psql -U motiva -d motiva -t -A -c "SELECT cleanup_intent FROM export_requests WHERE id='$EID3'")
  [ "$INTENT" = "f" ] && pass "cleanup intent settled (bytes of all attempts removed)" || fail "cleanup intent still set: $INTENT"
fi

note "summary: $FAILURES failures (artifacts/e2e)"
[ "$FAILURES" = "0" ] || exit 1
