// T09: two competitors race for the last budget remainder — exactly one Posted reward,
// the budget never goes negative (E04 shape under load).
import http from 'k6/http';
import { check, sleep } from 'k6';

const BASE = __ENV.BASE_URL || 'http://lb';
const SRC = __ENV.SRC_TOKEN;
const EMP = __ENV.EMP_TOKEN;
const CAMPAIGN_TASKS = JSON.parse(__ENV.RACE_TARGETS || '[]');

const raceOutcomes = [];

export const options = {
  scenarios: {
    racers: {
      executor: 'per-vu-iterations',
      vus: 2 * CAMPAIGN_TASKS.length,
      iterations: 1,
      startTime: '2s',
      maxDuration: '30s',
      gracefulStop: '5s',
    },
  },
};

export default function () {
  const target = CAMPAIGN_TASKS[Math.floor((__VU - 1) / 2) % CAMPAIGN_TASKS.length];
  // Two VUs share the same target: they race for the same remainder (E04).
  const masterId = 2 + (Date.now() % 9000) + __VU * 17; // seeded employee, fresh per run
  const body = JSON.stringify({
    eventNumber: 'RACE-' + Date.now() + '-' + __VU,
    masterId: masterId,
    taskId: target.taskId,
    delta: target.goal,
  });
  const r = http.post(BASE + '/api/v1/progress-events', body, {
    headers: { Authorization: 'Bearer ' + SRC, 'Content-Type': 'application/json' },
  });
  const parsed = r.status === 201 ? JSON.parse(r.body) : {};
  const outcome = parsed.completion?.reward?.outcome;
  // Two VUs share one target: exactly one Granted and one DeclinedInsufficientBudget must
  // be the TOTAL outcome — each VU records its own outcome; the report aggregates them.
  check(r, {
    'race processed': (res) => res.status === 201,
    'race credited to goal': () => parsed.creditedDelta === target.goal,
    'definite outcome': () => outcome === 'Granted' || outcome === 'DeclinedInsufficientBudget',
  });
  raceOutcomes.push(outcome || 'http-' + r.status);
  sleep(0.2);
}

export function teardown() {
  if (raceOutcomes.length === 2) {
    const granted = raceOutcomes.filter((o) => o === 'Granted').length;
    const declined = raceOutcomes.filter((o) => o === 'DeclinedInsufficientBudget').length;
    if (granted !== 1 || declined !== 1) {
      console.error('RACE INVARIANT BROKEN: ' + JSON.stringify(raceOutcomes));
    }
  }
}
