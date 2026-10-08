// T09 shared business mix: 50% catalog, 25% own wallet, 10% live leaderboard, 10% new
// progress events, 5% new spends of 1 unit. One implementation serves the main profile and
// the degradation pass — the mix cannot drift between them. Reproducibility: a seeded PRNG
// (mulberry32, SEED env, default 42) drives the branch choice and user selection instead of
// Math.random(). Goodput counts CORRECT BUSINESS responses delivered within 1 s — not checks.
import http from 'k6/http';
import { check, sleep } from 'k6';
import { Counter, Rate } from 'k6/metrics';

export const failed = new Counter('motiva_failed_requests');
export const businessWrong = new Counter('motiva_business_wrong');
export const goodputRate = new Rate('motiva_goodput_rate');
export const eventsAccepted = new Counter('motiva_events_accepted');
export const spendsPosted = new Counter('motiva_spends_posted');

export const START_MS = Date.now();

export function makeRng(seed) {
  let a = seed >>> 0;
  return function () {
    a |= 0;
    a = (a + 0x6d2b79f5) | 0;
    let t = Math.imul(a ^ (a >>> 15), 1 | a);
    t = (t + Math.imul(t ^ (t >>> 7), 61 | t)) ^ t;
    return ((t ^ (t >>> 14)) >>> 0) / 4294967296;
  };
}

// The measurement window starts after the 60 s warmup stage; every request is tagged so the
// summary separates warmup from measurement (T09: 60 s прогрев + 300 с измерение).
export function phaseTag() {
  return Date.now() - START_MS >= 60000 ? 'measure' : 'warmup';
}

const headersFor = (token) => ({ Authorization: 'Bearer ' + token, 'Content-Type': 'application/json' });

export function runMix(env, rng) {
  const phase = phaseTag();
  const dice = rng();
  let response;
  let correct;
  if (dice < 0.50) {
    // catalog: employee-visible resources (Valkey 30 s snapshot or PG fallback)
    response = http.get(env.BASE + '/api/v1/resources', { headers: headersFor(env.EMP), tags: { kind: 'read', phase } });
    correct = check(response, { 'catalog 200': (r) => r.status === 200 });
  } else if (dice < 0.75) {
    response = http.get(env.BASE + '/api/v1/me/wallet', { headers: headersFor(env.EMP), tags: { kind: 'read', phase } });
    correct = check(response, { 'wallet 200': (r) => r.status === 200 });
  } else if (dice < 0.85) {
    if (env.CHALLENGES.length === 0) {
      // Fail loudly rather than silently dropping the 10% branch (adapter verification, D15).
      businessWrong.add(1);
      failed.add(1);
      sleep(0.05);
      return;
    }
    const challenge = env.CHALLENGES[Math.floor(rng() * env.CHALLENGES.length)];
    response = http.get(env.BASE + '/api/v1/challenges/' + challenge + '/leaderboard?limit=10', { headers: headersFor(env.EMP), tags: { kind: 'read', phase } });
    correct = check(response, { 'leaderboard 200': (r) => r.status === 200 });
  } else if (dice < 0.95) {
    const task = env.TASKS[Math.floor(rng() * env.TASKS.length)];
    const masterId = 2 + Math.floor(rng() * env.EMPLOYEES);
    const body = JSON.stringify({
      eventNumber: 'LOAD-' + Date.now() + '-' + Math.floor(rng() * 1e9),
      masterId: masterId,
      taskId: task,
      delta: 1,
    });
    response = http.post(env.BASE + '/api/v1/progress-events', body, { headers: headersFor(env.SRC), tags: { kind: 'write', phase } });
    // 201 is the transport result; the business result must be Accepted for the seeded audience.
    const parsed = response.status === 201 ? JSON.parse(response.body) : {};
    correct = check(response, {
      'event 201': (r) => r.status === 201,
      'event Accepted': () => parsed.result === 'Accepted',
    });
    if (parsed.result === 'Accepted') {
      eventsAccepted.add(1);
    }
  } else {
    const viaShop = rng() < 0.5;
    const body = {
      purchaseSystemId: env.SYSTEM_ID,
      resourceId: env.RESOURCE_ID,
      amount: 1,
      operationNumber: 'LOAD-S-' + Date.now() + '-' + Math.floor(rng() * 1e9),
    };
    if (viaShop) {
      // A service spend names the wallet owner (B23.2).
      body.masterId = 2 + Math.floor(rng() * env.EMPLOYEES);
    }
    const token = viaShop ? env.SHOP : env.EMP;
    response = http.post(env.BASE + '/api/v1/spends', JSON.stringify(body), { headers: headersFor(token), tags: { kind: 'write', phase } });
    // The seeded wallets are topped up for 1-unit spends: the business result must be Posted.
    const parsed = response.status === 201 ? JSON.parse(response.body) : {};
    correct = check(response, {
      'spend 201': (r) => r.status === 201,
      'spend Posted': () => parsed.result === 'Posted',
    });
    if (parsed.result === 'Posted') {
      spendsPosted.add(1);
    }
  }

  // Goodput (T09): the request produced the correct business result AND completed within 1 s.
  const good = correct && response.timings.duration <= 1000;
  goodputRate.add(good);
  if (!good) {
    failed.add(1);
  }
  sleep(0.05);
}
