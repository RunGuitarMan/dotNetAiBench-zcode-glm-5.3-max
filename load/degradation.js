// T09 degradation pass (D15): the SAME 100 RPS profile and the SAME five-branch mix as the
// main run keep executing while dependencies fail (Valkey 30 s, S3 30 s, one API 10 s —
// orchestrated by scripts/load.sh). No hard thresholds here: this pass documents the actual
// degradation and the recovery; the correctness gates live in main.js.
import { makeRng, runMix, goodputRate } from './mix.js';

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
    degradation: {
      executor: 'ramping-arrival-rate',
      startRate: 1,
      timeUnit: '1s',
      preAllocatedVUs: 50,
      maxVUs: 200,
      stages: [
        { target: 100, duration: '60s' },   // same warmup shape as the main profile
        { target: 100, duration: '300s' },  // the outage windows fall inside this stage
      ],
      exec: 'mix',
    },
  },
};

export default function () {
  const rng = makeRng(Number(__ENV.SEED || 42) + (__VU - 1) * 7919 + 104729);
  runMix(env, rng);
}
