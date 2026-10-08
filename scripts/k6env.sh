#!/usr/bin/env bash
# Emits "-e KEY=VALUE" pairs for the k6 load containers from the wrapper tokens and the
# seeded stand state (challenges, first tasks, shop, resource, the LAST race pair).
set -euo pipefail
cd "$(dirname "$0")/.."
python3 - <<'PY'
import json, subprocess
emp = open('auth/.local/tokens/employee123.jwt').read().strip()
src = open('auth/.local/tokens/progress-source.jwt').read().strip()
shop = open('auth/.local/tokens/shop-source.jwt').read().strip()

def q(sql):
    out = subprocess.run(['docker', 'compose', 'exec', '-T', 'postgres', 'psql', '-U', 'motiva',
                          '-d', 'motiva', '-t', '-A', '-F', ',', '-c', sql],
                         capture_output=True, text=True).stdout.strip()
    return [line for line in out.splitlines() if line]

tasks = q("SELECT t.id FROM tasks t JOIN streams s ON s.id=t.stream_id JOIN campaigns c ON c.id=s.campaign_id "
          "WHERE c.code_norm LIKE 'SEEDC%' AND t.code_norm='T00' AND c.company_id='11111111-1111-4111-8111-111111111111'")
challenges = q("SELECT ch.id FROM challenges ch JOIN campaigns c ON c.id=ch.campaign_id "
               "WHERE c.code_norm LIKE 'SEEDC%' AND c.company_id='11111111-1111-4111-8111-111111111111'")
system_rows = q("SELECT ps.id FROM purchase_systems ps WHERE ps.code_norm='SEEDSHOP'")
resource_rows = q("SELECT r.id FROM resources r WHERE r.code_norm='SEEDSTAR'")
if not challenges:
    raise SystemExit('k6env: no seeded challenges — run scripts/load.sh seed first (adapter verification, T09)')
if not system_rows or not resource_rows:
    raise SystemExit('k6env: no seeded shop/resource — run scripts/load.sh seed first')

# The race pair: the LATEST SEEDRACE campaign. Its budget must hold EXACTLY the reward (the
# genuinely last remainder) — the adapter refuses to run a race over a stale remainder.
race = []
race_rows = q(
    "SELECT t.goal, t.id, b.allocated_total - b.spent_total + b.returned_total "
    "FROM campaigns c "
    "JOIN budgets b ON b.campaign_id = c.id "
    "JOIN streams s ON s.campaign_id = c.id "
    "JOIN tasks t ON t.stream_id = s.id "
    "WHERE c.code_norm LIKE 'SEEDRACE%' AND t.code_norm='TR' "
    "  AND c.company_id='11111111-1111-4111-8111-111111111111' "
    "ORDER BY c.created_at DESC LIMIT 1")
import os
require_race = os.environ.get('K6_REQUIRE_RACE') == '1'
if not race_rows:
    if require_race:
        raise SystemExit('k6env: no race pair — run scripts/load.sh seed first (T09 competitors)')
    print('k6env: no unused race pair (only needed by competitors)', file=__import__('sys').stderr)
else:
    goal, tid, remainder = race_rows[0].split(',')
    goal, remainder = int(goal), int(remainder)
    if remainder != 10 or goal != 3:
        if require_race:
            raise SystemExit(f'k6env: race pair remainder is {remainder}, goal {goal} — expected exactly 10/3; re-run seed')
        print(f'k6env: race pair consumed (remainder {remainder}) — fine for non-race scenarios', file=__import__('sys').stderr)
    else:
        race.append({'goal': goal, 'taskId': tid})
        print(f'k6env: race pair OK (goal={goal}, remainder={remainder})', file=__import__('sys').stderr)

pairs = ['-e', 'EMP_TOKEN=' + emp,
         '-e', 'SRC_TOKEN=' + src,
         '-e', 'SHOP_TOKEN=' + shop,
         '-e', 'BASE_URL=http://lb',
         '-e', 'TASK_IDS=' + ','.join(tasks),
         '-e', 'CHALLENGE_IDS=' + ','.join(challenges),
         '-e', 'PURCHASE_SYSTEM_ID=' + system_rows[0],
         '-e', 'RESOURCE_ID=' + resource_rows[0],
         '-e', 'EMPLOYEE_MAX=10002',
         '-e', 'SEED=42',
         '-e', 'RACE_TARGETS=' + json.dumps(race, separators=(',', ':'))]
print(' '.join(pairs))
PY
