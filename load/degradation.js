// T09 degradation pass: the SAME business mix keeps running while dependencies fail;
// the run records the actual degradation (checks may fail only while a dependency is down
// and the service must recover without manual action).
import http from 'k6/http';
import { check, sleep } from 'k6';
import { Counter } from 'k6/metrics';

const BASE = __ENV.BASE_URL || 'http://lb';
const EMP = __ENV.EMP_TOKEN;
const SRC = __ENV.SRC_TOKEN;
const TASKS = (__ENV.TASK_IDS || '').split(',').filter(Boolean);
const failed = new Counter('motiva_failed_requests');

export const options = {
  scenarios: {
    degradation: {
      executor: 'constant-arrival-rate',
      rate: 50,
      timeUnit: '1s',
      duration: '120s',
      preAllocatedVUs: 30,
      maxVUs: 100,
    },
  },
  // No hard thresholds: this pass documents the degradation profile, the gates live in main.js.
};

export default function () {
  const dice = Math.random();
  if (dice < 0.6) {
    const r = http.get(BASE + '/api/v1/resources', { headers: { Authorization: 'Bearer ' + EMP }, tags: { kind: 'read' } });
    check(r, { 'catalog 200': (res) => res.status === 200 }) || failed.add(1);
  } else if (dice < 0.9) {
    const r = http.get(BASE + '/api/v1/me/wallet', { headers: { Authorization: 'Bearer ' + EMP }, tags: { kind: 'read' } });
    check(r, { 'wallet 200': (res) => res.status === 200 }) || failed.add(1);
  } else {
    const body = JSON.stringify({
      eventNumber: 'DEG-' + Date.now() + '-' + Math.floor(Math.random() * 1e9),
      masterId: 2 + Math.floor(Math.random() * 10000),
      taskId: TASKS[Math.floor(Math.random() * TASKS.length)],
      delta: 1,
    });
    const r = http.post(BASE + '/api/v1/progress-events', body,
      { headers: { Authorization: 'Bearer ' + SRC, 'Content-Type': 'application/json' }, tags: { kind: 'write' } });
    check(r, { 'event 201': (res) => res.status === 201 }) || failed.add(1);
  }
  sleep(0.05);
}
