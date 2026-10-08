// T09 open-model load profile: 100 RPS, 60 s warmup + 300 s measurement.
// The mix (50/25/10/10/5) and the seeded PRNG live in mix.js — shared with the degradation
// pass so the profile cannot drift between them (D15).
import { makeRng, runMix, goodputRate, failed } from './mix.js';

const env = {
  BASE: __ENV.BASE_URL || 'http://lb',
  EMP: __ENV.EMP_TOKEN,
  SRC: __ENV.SRC_TOKEN,
  SHOP: __ENV.SHOP_TOKEN,
  CAMPAIGNS: (__ENV.CAMPAIGN_IDS || '').split(',').filter(Boolean),
  TASKS: (__ENV.TASK_IDS || '').split(',').filter(Boolean),
  CHALLENGES: (__ENV.CHALLENGE_IDS || '').split(',').filter(Boolean),
  SYSTEM_ID: __ENV.PURCHASE_SYSTEM_ID,
  RESOURCE_ID: __ENV.RESOURCE_ID,
  EMPLOYEES: Number(__ENV.EMPLOYEE_MAX || 10002),
};

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
    'http_req_failed{phase:measure}': ['rate<0.005'],
    'http_req_duration{kind:read,phase:measure}': ['p(95)<200', 'p(99)<1000'],
    'http_req_duration{kind:write,phase:measure}': ['p(95)<500', 'p(99)<1000'],
    // Goodput is a first-class gate: ≥ 99.5 % of requests return the correct business result
    // within 1 s (T09), counted per request — not per check.
    goodputRate: ['rate>=0.995'],
    failed: ['count<1'],
  },
};

export default function () {
  // One PRNG per VU, all seeded from the run-level SEED: the whole run is reproducible.
  const rng = makeRng(Number(__ENV.SEED || 42) + (__VU - 1) * 7919);
  runMix(env, rng);
}
