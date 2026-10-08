#!/usr/bin/env python3
"""T09 reconciliation (D15): compares what the generator OBSERVED (k6 summary-export JSON)
with what the application actually SAVED (PostgreSQL). It detects a lost operation (k6 saw
201/Accepted, the DB has no event), a wrong result and a duplicated effect (two operations
for one unique number). Exits non-zero on any mismatch — a green load run without this check
proves nothing about saved effects.
"""
import datetime
import json
import pathlib
import subprocess
import sys

# The reconciliation window: rows created at/after the main run's start (its file is written by
# scripts/load.sh); without it the comparison would mix earlier runs into the DB side.
START_FILE = pathlib.Path('artifacts/load/main.started')
SINCE = START_FILE.read_text().strip() if START_FILE.exists() else (
    datetime.datetime.now(datetime.timezone.utc) - datetime.timedelta(minutes=7)
).strftime('%Y-%m-%dT%H:%M:%SZ')

def q(sql):
    out = subprocess.run(['docker', 'compose', 'exec', '-T', 'postgres', 'psql', '-U', 'motiva',
                          '-d', 'motiva', '-t', '-A', '-c', sql],
                         capture_output=True, text=True)
    if out.returncode != 0:
        raise SystemExit('psql failed: ' + out.stderr)
    return out.stdout.strip()

def counter(summary, name):
    try:
        with open(summary) as f:
            data = json.load(f)
        return int(data['metrics'][name]['count'])
    except FileNotFoundError:
        return None

def one(sql):
    return int(q(sql) or 0)

failures = []

# 1) Expected vs saved progress events (LOAD- numbers are unique per request).
k6_events = counter('artifacts/load/main.summary.json', 'motiva_events_accepted')
db_events = one(f"SELECT count(*) FROM progress_events pe WHERE pe.event_number LIKE 'LOAD-%' AND pe.accepted_at >= '{SINCE}'")
print(f'progress events since {SINCE}: k6 accepted={k6_events}, saved={db_events}')
if k6_events is None:
    print('  (main.summary.json missing — run the load with --summary-export)')
elif k6_events != db_events:
    failures.append(f'progress events lost/duplicated: k6={k6_events}, db={db_events}')

# 2) Expected vs saved spends.
k6_spends = counter('artifacts/load/main.summary.json', 'motiva_spends_posted')
db_spends = one(f"SELECT count(*) FROM operations o WHERE o.source_number LIKE 'LOAD-S-%' AND o.result = 'Posted' AND o.created_at >= '{SINCE}'")
print(f'spends: k6 posted={k6_spends}, saved posted={db_spends}')
if k6_spends is None:
    print('  (main.summary.json missing)')
elif k6_spends != db_spends:
    failures.append(f'spends lost/duplicated: k6={k6_spends}, db={db_spends}')

# 3) No duplicated numbers and no lost rewards: LOAD- event numbers are unique; every accepted
#    completion number maps to at most one operation per kind.
dup_numbers = one(f"SELECT count(*) FROM (SELECT source_number, count(*) c FROM operations WHERE source_number LIKE 'LOAD-%' AND created_at >= '{SINCE}' GROUP BY source_number HAVING count(*) > 1) d")
print(f'duplicated LOAD- operation numbers: {dup_numbers}')
if dup_numbers:
    failures.append(f'{dup_numbers} duplicated LOAD- operation numbers')

# 4) Invariants: no negative budgets or balances, no duplicate event numbers.
negative_budgets = one("SELECT count(*) FROM budgets b WHERE b.allocated_total - b.spent_total + b.returned_total < 0")
negative_balances = one("SELECT count(*) FROM wallet_balances w WHERE w.balance < 0")
dup_event_numbers = one(f"SELECT count(*) FROM (SELECT company_id, source_subject, event_number, count(*) c FROM progress_events WHERE event_number LIKE 'LOAD-%' AND accepted_at >= '{SINCE}' GROUP BY 1,2,3 HAVING count(*) > 1) d")
print(f'negative budgets={negative_budgets}, negative balances={negative_balances}, duplicated event numbers={dup_event_numbers}')
if negative_budgets:
    failures.append(f'{negative_budgets} negative budgets')
if negative_balances:
    failures.append(f'{negative_balances} negative balances')
if dup_event_numbers:
    failures.append(f'{dup_event_numbers} duplicated event numbers')

# 5) The race pair: the latest SEEDRACE campaign must end with exactly one Posted TaskReward,
#    one DeclinedInsufficientBudget and a zero remainder.
race = q("SELECT c.code_norm FROM campaigns c WHERE c.code_norm LIKE 'SEEDRACE%' ORDER BY c.created_at DESC LIMIT 1")
if race:
    posted = one(f"SELECT count(*) FROM operations o JOIN campaigns c ON c.id = o.campaign_id WHERE c.code_norm = '{race}' AND o.kind = 'TaskReward' AND o.result = 'Posted'")
    declined = one(f"SELECT count(*) FROM operations o JOIN campaigns c ON c.id = o.campaign_id WHERE c.code_norm = '{race}' AND o.kind = 'TaskReward' AND o.result = 'Declined'")
    remainder = q(f"SELECT b.allocated_total - b.spent_total + b.returned_total FROM budgets b JOIN campaigns c ON c.id = b.campaign_id WHERE c.code_norm = '{race}'")
    print(f'race {race}: posted={posted}, declined={declined}, remainder={remainder}')
    if posted != 1 or declined != 1 or remainder != '0':
        failures.append(f'race {race}: posted={posted}, declined={declined}, remainder={remainder} — expected 1/1/0')
else:
    print('race: no SEEDRACE campaign found (seed not run)')

if failures:
    print('\nRECONCILIATION FAILED:')
    for f in failures:
        print(' - ' + f)
    sys.exit(1)
print('\nRECONCILIATION OK: expected == saved for every checked effect')
