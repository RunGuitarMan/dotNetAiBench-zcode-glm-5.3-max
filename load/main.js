// T09 open-model load profile: 100 RPS, 60 s warmup + 300 s measurement.
// Mix: 50% catalog, 25% own wallet, 10% live leaderboard, 10% new progress events,
// 5% new spends (1 unit). Routes follow docs/openapi.yaml.
import http from 'k6/http';
import { check, sleep } from 'k6';
import { Counter } from 'k6/metrics';

const BASE = __ENV.BASE_URL || 'http://lb';
const EMP = __ENV.EMP_TOKEN;
const SRC = __ENV.SRC_TOKEN;
const SHOP = __ENV.SHOP_TOKEN;
const CAMPAIGNS = (__ENV.CAMPAIGN_IDS || '').split(',').filter(Boolean);
const TASKS = (__ENV.TASK_IDS || '').split(',').filter(Boolean);
const SYSTEM_ID = __ENV.PURCHASE_SYSTEM_ID;
const RESOURCE_ID = __ENV.RESOURCE_ID;
const EMPLOYEES = Number(__ENV.EMPLOYEE_MAX || 10002);

const failed = new Counter('motiva_failed_requests');
const CHALLENGES = (__ENV.CHALLENGE_IDS || '').split(',').filter(Boolean);

export const options = {
  scenarios: {
    openload: {
      executor: 'ramping-arrival-rate',
      startRate: 1,
      timeUnit: '1s',
      preAllocatedVUs: 50,
      maxVUs: 200,
      stages: [
        { target: 100, duration: '60s' },   // warmup
        { target: 100, duration: '300s' },  // measurement
      ],
      exec: 'mix',
    },
  },
  thresholds: {
    http_req_failed: ['rate<0.005'],
    'http_req_duration{kind:read}': ['p(95)<200', 'p(99)<1000'],
    'http_req_duration{kind:write}': ['p(95)<500', 'p(99)<1000'],
  },
};

const headersFor = (token) => ({ Authorization: 'Bearer ' + token, 'Content-Type': 'application/json' });

export function mix() {
  const dice = Math.random();
  if (dice < 0.50) {
    // catalog: employee-visible resources (Valkey 30 s snapshot or PG fallback)
    const r = http.get(BASE + '/api/v1/resources', { headers: headersFor(EMP), tags: { kind: 'read' } });
    check(r, { 'catalog 200': (res) => res.status === 200 }) || failed.add(1);
  } else if (dice < 0.75) {
    const r = http.get(BASE + '/api/v1/me/wallet', { headers: headersFor(EMP), tags: { kind: 'read' } });
    check(r, { 'wallet 200': (res) => res.status === 200 }) || failed.add(1);
  } else if (dice < 0.85) {
    const challenge = CHALLENGES[Math.floor(Math.random() * CHALLENGES.length)][Math.floor(Math.random() * CHALLENGES.length)];
    const r = http.get(BASE + '/api/v1/challenges/' + challenge + '/leaderboard?limit=10', { headers: headersFor(EMP), tags: { kind: 'read' } });
    check(r, { 'leaderboard 200': (res) => res.status === 200 }) || failed.add(1);
  } else if (dice < 0.95) {
    const task = TASKS[Math.floor(Math.random() * TASKS.length)];
    const masterId = 2 + Math.floor(Math.random() * EMPLOYEES);
    const body = JSON.stringify({
      eventNumber: 'LOAD-' + Date.now() + '-' + Math.floor(Math.random() * 1e9),
      masterId: masterId,
      taskId: task,
      delta: 1,
    });
    const r = http.post(BASE + '/api/v1/progress-events', body, { headers: headersFor(SRC), tags: { kind: 'write' } });
    check(r, { 'event 201': (res) => res.status === 201 }) || failed.add(1);
  } else {
    const viaShop = Math.random() < 0.5;
    const body = {
      purchaseSystemId: SYSTEM_ID,
      resourceId: RESOURCE_ID,
      amount: 1,
      operationNumber: 'LOAD-S-' + Date.now() + '-' + Math.floor(Math.random() * 1e9),
    };
    if (viaShop) {
      // A service spend names the wallet owner (B23.2).
      body.masterId = 2 + Math.floor(Math.random() * EMPLOYEES);
    }

    const token = viaShop ? SHOP : EMP;
    const r = http.post(BASE + '/api/v1/spends', JSON.stringify(body), { headers: headersFor(token), tags: { kind: 'write' } });
    check(r, { 'spend 201': (res) => res.status === 201 }) || failed.add(1);
  }
  sleep(0.05);
}

