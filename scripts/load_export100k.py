#!/usr/bin/env python3
"""T09/T07: order a company-wide export, wait for Ready (<=120 s), download the bytes and
verify header/checksum/row-count against the committed movements in the database."""
import hashlib
import json
import subprocess
import time
import urllib.request

BASE = 'http://localhost:8080'
TOKEN = open('auth/.local/tokens/admin1.jwt').read().strip()


def api(method, path, body=None, key=None):
    headers = {'Authorization': 'Bearer ' + TOKEN, 'Content-Type': 'application/json'}
    if key:
        headers['Idempotency-Key'] = key
    req = urllib.request.Request(BASE + path, method=method,
                                 data=json.dumps(body).encode() if body else None, headers=headers)
    with urllib.request.urlopen(req) as resp:
        return json.load(resp) if resp.status != 204 else None


exp = api('POST', '/api/v1/exports',
          {'scope': 'Company', 'fromUtc': '2020-01-01T00:00:00Z', 'toUtc': '2030-01-01T00:00:00Z'},
          key='load-100k-' + str(int(time.time())))
eid = exp['id']
t0 = time.time()
status = None
while time.time() - t0 < 300:
    status = api('GET', '/api/v1/exports/' + eid)['status']
    if status in ('Ready', 'Error', 'Deleted'):
        break
    time.sleep(2)
elapsed = time.time() - t0
print(f'export status={status} elapsed={elapsed:.1f}s')
assert status == 'Ready', f'export not ready: {status}'
assert elapsed <= 120, f'export took {elapsed:.1f}s > 120s'
meta = api('GET', '/api/v1/exports/' + eid)
print('sizeBytes', meta.get('sizeBytes'), 'checksum', (meta.get('checksum') or '')[:16])

link = api('POST', '/api/v1/exports/' + eid + '/download-links', None,
           key='load-100k-dl-' + str(int(time.time())))
with urllib.request.urlopen(link['url']) as resp:
    csv_bytes = resp.read()
assert hashlib.sha256(csv_bytes).hexdigest() == meta['checksum'].lower(), 'downloaded bytes differ from checksum'
text = csv_bytes.decode()
lines = text.split('\n')
assert lines[0] == 'operationId,masterId,resourceCode,amount,kind,campaignId,originalOperationId,createdAtUtc', 'bad header'
rows = [l for l in lines[1:] if l]
print('csv rows:', len(rows))
assert len(rows) >= 100000, f'expected >=100k rows, got {len(rows)}'

sql = ("SELECT count(*) FROM operation_items oi JOIN operations o ON o.id = oi.operation_id "
       "WHERE o.result = 'Posted' AND o.company_id = '11111111-1111-4111-8111-111111111111' "
       "AND o.kind IN ('TaskReward','ManualAward','Spend','SpendReversal','AwardReversal') "
       "AND o.created_at >= '2020-01-01' AND o.created_at < '2030-01-01'")
dbcount = subprocess.run(['docker', 'compose', 'exec', '-T', 'postgres', 'psql', '-U', 'motiva',
                          '-d', 'motiva', '-t', '-A', '-c', sql],
                         capture_output=True, text=True).stdout.strip()
print('committed movements in range:', dbcount)
assert int(dbcount) == len(rows), f'csv rows {len(rows)} != committed movements {dbcount}'
print('EXPORT_OK')
