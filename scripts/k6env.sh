#!/usr/bin/env bash
# Emits "-e KEY=VALUE" pairs for the k6 load containers from the wrapper tokens and the
# seeded stand state (challenges, first tasks, shop, resource).
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

race = []
for row in q("SELECT t.goal, t.id FROM tasks t JOIN streams s ON s.id=t.stream_id JOIN campaigns c ON c.id=s.campaign_id "
             "WHERE c.code_norm='SEEDC00' AND t.code_norm='T01' AND c.company_id='11111111-1111-4111-8111-111111111111'"):
    goal, tid = row.split(',')
    race.append({'goal': int(goal), 'taskId': tid})

pairs = ['-e', 'EMP_TOKEN=' + emp,
         '-e', 'SRC_TOKEN=' + src,
         '-e', 'SHOP_TOKEN=' + shop,
         '-e', 'BASE_URL=http://lb',
         '-e', 'TASK_IDS=' + ','.join(tasks),
         '-e', 'CHALLENGE_IDS=' + ','.join(challenges),
         '-e', 'PURCHASE_SYSTEM_ID=' + system_rows[0],
         '-e', 'RESOURCE_ID=' + resource_rows[0],
         '-e', 'EMPLOYEE_MAX=10002',
         '-e', 'RACE_TARGETS=' + json.dumps(race, separators=(',', ':'))]
print(' '.join(pairs))
PY
