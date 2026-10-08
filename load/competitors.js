// T09: two competitors race for the genuinely LAST budget remainder — exactly one Posted
// reward, the budget never goes negative (E04 shape under load). The raced pair is prepared
// by the seed with budget == reward (see tools/Motiva.Seed): two VUs send one completing
// event each and the TOTAL outcome is asserted through k6 Counter thresholds: counters
// aggregate ACROSS VUs (a VU-local array is invisible to teardown — the R1 mistake), and a
// violated threshold FAILS the run instead of console.error.
import http from 'k6/http';
import { check, sleep } from 'k6';
import { Counter } from 'k6/metrics';

const BASE = __ENV.BASE_URL || 'http://lb';
const SRC = __ENV.SRC_TOKEN;
const CAMPAIGN_TASKS = JSON.parse(__ENV.RACE_TARGETS || '[]');

const raceGranted = new Counter('motiva_race_granted');
const raceDeclined = new Counter('motiva_race_declined');
const raceBad = new Counter('motiva_race_bad');

const pairs = CAMPAIGN_TASKS.length;

export const options = {
  scenarios: {
    racers: {
      executor: 'per-vu-iterations',
      vus: 2 * pairs,
      iterations: 1,
      startTime: '2s',
      maxDuration: '30s',
      gracefulStop: '5s',
    },
  },
  thresholds: {
    // Aggregate over ALL VUs: each raced pair contributes exactly one Granted and one
    // DeclinedInsufficientBudget — the invariant of the last remainder (E04).
    motiva_race_granted: ['count == ' + pairs],
    motiva_race_declined: ['count == ' + pairs],
    motiva_race_bad: ['count == 0'],
  },
};

export default function () {
  const target = CAMPAIGN_TASKS[Math.floor((__VU - 1) / 2) % pairs];
  // Two VUs share one target and race for the same remainder; racers are distinct seeded
  // employees of the main company.
  const masterId = 2 + ((__VU * 613) % 9000) + 900;
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
  check(r, {
    'race processed': (res) => res.status === 201,
    'race credited to goal': () => parsed.creditedDelta === target.goal,
    'definite outcome': () => outcome === 'Granted' || outcome === 'DeclinedInsufficientBudget',
  });
  if (outcome === 'Granted') {
    raceGranted.add(1);
  } else if (outcome === 'DeclinedInsufficientBudget') {
    raceDeclined.add(1);
  } else {
    raceBad.add(1);
  }
  sleep(0.2);
}
