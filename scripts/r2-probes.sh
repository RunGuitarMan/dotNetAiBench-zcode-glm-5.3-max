#!/usr/bin/env bash
# R2 semantic probes (stage-4 list): six valid business-meaning mutations, one per isolated
# branch. Before each mutation the ORIGINAL scenario must pass; the mutated variant must
# COMPILE; the probe counts only when the suite FAILS for the required business reason. After
# each probe the original file state is restored and the branch deleted.
set -uo pipefail
cd "$(dirname "$0")/.."
mkdir -p artifacts/r2/probes
export Motiva__PostgresConnectionString="Host=localhost;Port=5433;Database=motiva;Username=motiva;Password=motiva-stand"
export Motiva__ValkeyEndpoint="localhost:6380"
export Motiva__S3ServiceUrl="http://localhost:9100"
export Motiva__S3AccessKey="motiva"
export Motiva__S3SecretKey="motiva-stand-secret"
export MOTIVA_TEST_SIGNING_KEY="dGVzdC1zaWduaW5nLWtleS0zMi1ieXRlcy1hYmMtZGVmIQ=="
BASE=$(git rev-parse --abbrev-ref HEAD)
RESULTS=artifacts/r2/probes/summary.txt
: > "$RESULTS"

mutate() { # mutate FILE OLD NEW
  python3 - "$1" "$2" "$3" <<'PY'
import pathlib, sys
p = pathlib.Path(sys.argv[1]); s = p.read_text(); old = sys.argv[2]; new = sys.argv[3]
assert old in s, 'mutation anchor not found: ' + old
p.write_text(s.replace(old, new, 1))
PY
}

probe() { # probe NAME FILE OLD NEW TESTFILTER EXPECTED_ASSERT
  local name=$1 file=$2 old=$3 new=$4 filter=$5 expect=$6
  echo "=== probe $name" | tee -a "$RESULTS"
  git checkout -q -b "probe/$name"

  # 1. The original scenario passes before the mutation.
  dotnet test "$TEST_PROJECT" --no-restore --filter "$filter" 2>&1 | tail -1 > /tmp/probe-before.log
  if ! grep -q "Пройден" /tmp/probe-before.log; then
    echo "probe $name: INVALID — original scenario does not pass: $(cat /tmp/probe-before.log)" | tee -a "$RESULTS"
    restore "$name"; return
  fi

  # 2. Apply the mutation and compile.
  if ! mutate "$file" "$old" "$new"; then
    echo "probe $name: INVALID — anchor missing" | tee -a "$RESULTS"
    restore "$name"; return
  fi
  if ! dotnet build Motiva.sln --no-restore >/dev/null 2>&1; then
    echo "probe $name: INVALID — mutation does not compile (build error is not a detection)" | tee -a "$RESULTS"
    restore "$name"; return
  fi

  # 3. Detection: the suite must FAIL for the required business reason.
  local out
  out=$(dotnet test "$TEST_PROJECT" --no-restore --no-build --filter "$filter" 2>&1)
  echo "$out" > "artifacts/r2/probes/$name.log"
  if echo "$out" | grep -q "Не пройден\|Failed"; then
    if echo "$out" | grep -q "$expect"; then
      echo "probe $name: DETECTED (failed on: $expect)" | tee -a "$RESULTS"
    else
      echo "probe $name: detected but WRONG REASON (expected '$expect'):" | tee -a "$RESULTS"
      echo "$out" | grep -A3 "Сообщение об ошибке" | head -8 | tee -a "$RESULTS"
    fi
  else
    echo "probe $name: NOT DETECTED — the suite stayed green" | tee -a "$RESULTS"
  fi

  restore "$name"
}

restore() { # restore NAME — back to the base with a byte-identical working tree
  git checkout -q "$BASE" 2>/dev/null
  git branch -qD "probe/$1" 2>/dev/null
  git checkout -q "$BASE" -- src/ tests/ load/ scripts/ tools/
  dotnet build Motiva.sln --no-restore >/dev/null 2>&1
}

TEST_PROJECT=tests/Motiva.Tests.Functional

# M1: reward refused at EXACT budget equality (E01 anchor: budget 12, cost 8+5 → second item
# hits Available == 5 == Amount; the mutation refuses it).
probe m1-budget-equality \
  src/Motiva.Application/Progress/ProgressEventsService.cs \
  'if (budget is null || budget.Available < item.Amount)' \
  'if (budget is null || budget.Available <= item.Amount)' \
  "FullyQualifiedName~e01_two_resources" \
  "both_budgets_zero\|BudgetAvailable\|budget"

# M2: a stored Declined spend re-debits after a top-up — BOTH replay guards (early and
# racing) ignore refusals, so after a top-up the same number executes again (custom: two sites).
echo "=== probe m2-declined-replay-redebts" | tee -a "$RESULTS"
git checkout -q -b probe/m2
if dotnet test "$TEST_PROJECT" --no-restore --filter "FullyQualifiedName~scn03_declined_spend_replays" 2>&1 | tail -1 | grep -q "Пройден"; then
  echo "  original scenario passes" | tee -a "$RESULTS"
  python3 - <<'PY'
import pathlib
p = pathlib.Path("src/Motiva.Application/Economy/SpendsService.cs")
s = p.read_text()
s = s.replace("if (existing is not null)\n        {\n            return Replay(existing, essential);",
              "if (existing is not null && existing.Result == OperationResult.Posted)\n        {\n            return Replay(existing, essential);", 1)
s = s.replace("if (racing is not null)\n        {\n            return Replay(racing, essential);",
              "if (racing is not null && racing.Result == OperationResult.Posted)\n        {\n            return Replay(racing, essential);", 1)
p.write_text(s)
PY
  if ! dotnet build Motiva.sln --no-restore >/dev/null 2>&1; then
    echo "probe m2: INVALID — mutation does not compile" | tee -a "$RESULTS"
  else
    out=$(dotnet test "$TEST_PROJECT" --no-restore --no-build --filter "FullyQualifiedName~scn03_declined_spend_replays" 2>&1)
    echo "$out" > artifacts/r2/probes/m2-declined-replay-redebts.log
    if echo "$out" | grep -q "Не пройден\|Failed"; then
      echo "probe m2: DETECTED (see log)" | tee -a "$RESULTS"
    else
      echo "probe m2: NOT DETECTED — stayed green" | tee -a "$RESULTS"
    fi
  fi
else
  echo "probe m2: INVALID — original scenario fails" | tee -a "$RESULTS"
fi
restore m2

# M3: the already-reversed guard is removed — a second reversal returns funds again.
probe m3-double-reversal \
  src/Motiva.Application/Economy/ReversalsService.cs \
  '        else if (await operations.FindPostedReversalAsync(actor.CompanyId, originalOperationId, ct) is not null)
        {
            refusal = RefusalCode.OriginalAlreadyReversed;
        }
' \
  '' \
  "FullyQualifiedName~e07_two_parallel_reversals" \
  "Assert\|баланс\|wallet"

# M4: the challenge score counts the TRANSMITTED delta instead of the credited one.
probe m4-score-transmitted \
  src/Motiva.Application/Progress/ProgressEventsService.cs \
  'await board.AddScoreAsync(challenge.Id, masterId, credited, ct);' \
  'await board.AddScoreAsync(challenge.Id, masterId, delta, ct);' \
  "FullyQualifiedName~e09_challenge_counts_credited" \
  "100\|score\|Place\|place"

# M5: a user operation debits ANOTHER masterId's wallet.
probe m5-foreign-wallet \
  src/Motiva.Application/Economy/SpendsService.cs \
  '            if (masterId is not null && masterId != actor.MasterId)
            {
                throw new MotivaException(ErrorCode.AuthzForbidden, "An employee can spend only from their own wallet.");
            }' \
  '            if (false)
            {
                throw new MotivaException(ErrorCode.AuthzForbidden, "An employee can spend only from their own wallet.");
            }' \
  "FullyQualifiedName~m5_user_spend" \
  "AuthzForbidden\|910\|wallet"

# M6: a compilable forbidden endpoint reference to Infrastructure (a NON-banned type — the
# compile-time RS0030 gate must stay silent; the NetArchTest dependency rule must fire).
git checkout -q -b probe/m6-endpoint-infrastructure
cat > src/Motiva.Api/Endpoints/ProbeEndpoint.cs <<'EOF'
namespace Motiva.Api.Endpoints;

// Probe (M6): a compilable architectural violation — a mapped endpoint touching an
// Infrastructure persistence type directly instead of going through a use case.
internal static class ProbeEndpoint
{
    public static string Describe(Motiva.Infrastructure.Persistence.Stores.EmployeeStore store)
    {
        return store.GetType().FullName ?? "unknown";
    }
}
EOF
if ! dotnet build Motiva.sln --no-restore >/dev/null 2>&1; then
  echo "probe m6: INVALID — does not compile" | tee -a "$RESULTS"
else
  out=$(dotnet test tests/Motiva.Tests.Architecture --no-restore --no-build 2>&1)
  echo "$out" > artifacts/r2/probes/m6-endpoint-infrastructure.log
  if echo "$out" | grep -q "Endpoints_and_worker_orchestration\|Не пройден"; then
    echo "probe m6: DETECTED (architecture dependency rule failed on the endpoint)" | tee -a "$RESULTS"
  else
    echo "probe m6: NOT DETECTED — architecture tests stayed green" | tee -a "$RESULTS"
  fi
fi
git checkout -q "$BASE"
git branch -qD probe/m6-endpoint-infrastructure
rm -f src/Motiva.Api/Endpoints/ProbeEndpoint.cs
dotnet build Motiva.sln --no-restore >/dev/null 2>&1

echo "=== probes complete; base restored on $BASE" | tee -a "$RESULTS"
git status --short | head -5
