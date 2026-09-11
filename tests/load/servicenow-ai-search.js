import http from 'k6/http';
import { check, fail } from 'k6';
import encoding from 'k6/encoding';
import { Rate, Trend, Counter } from 'k6/metrics';

const errorRate = new Rate('servicenow_errors');
const throttles = new Counter('servicenow_429s');
const timeouts = new Counter('servicenow_timeouts');
const latency = new Trend('servicenow_latency', true);

function required(name) {
  const value = __ENV[name];
  if (!value) fail(`${name} is required`);
  return value;
}

function positiveInteger(name) {
  const value = Number(required(name));
  if (!Number.isInteger(value) || value <= 0) fail(`${name} must be a positive integer`);
  return value;
}

const approved = __ENV.SERVICENOW_LOAD_TEST_APPROVED === 'true';
if (!approved) fail('SERVICENOW_LOAD_TEST_APPROVED=true is required');

const endpoint = required('SERVICENOW_SEARCH_ENDPOINT');
const tokenEndpoint = required('SERVICENOW_TOKEN_ENDPOINT');
const syntheticUpn = required('SERVICENOW_SYNTHETIC_UPN');
const syntheticQuery = required('SERVICENOW_SYNTHETIC_QUERY');
const targetRps = positiveInteger('SERVICENOW_TARGET_RPS');
const approvedRps = positiveInteger('SERVICENOW_APPROVED_MAX_RPS');
const approvedVus = positiveInteger('SERVICENOW_APPROVED_MAX_VUS');
const duration = required('SERVICENOW_TEST_DURATION');

for (const urlText of [endpoint, tokenEndpoint]) {
  const match = /^https:\/\/([^/:?#]+)/i.exec(urlText);
  const host = match ? match[1].toLowerCase() : '';
  if (!match || (!host.includes('dev') && !host.includes('uat') && !host.includes('test'))) {
    fail('Only an explicit HTTPS non-production ServiceNow host is permitted');
  }
}
if (targetRps > approvedRps) fail('SERVICENOW_TARGET_RPS exceeds SERVICENOW_APPROVED_MAX_RPS');

export const options = {
  discardResponseBodies: true,
  scenarios: {
    approved_nonproduction_search: {
      executor: 'ramping-arrival-rate',
      startRate: 1,
      timeUnit: '1s',
      preAllocatedVUs: Math.min(approvedVus, Math.max(1, targetRps)),
      maxVUs: approvedVus,
      stages: [
        { target: targetRps, duration: '30s' },
        { target: targetRps, duration },
        { target: 0, duration: '15s' },
      ],
    },
  },
  thresholds: {
    servicenow_errors: [{ threshold: `rate<${__ENV.SERVICENOW_ABORT_ERROR_RATE || '0.05'}`, abortOnFail: true, delayAbortEval: '15s' }],
    servicenow_429s: [{ threshold: `count<${__ENV.SERVICENOW_ABORT_429_COUNT || '10'}`, abortOnFail: true, delayAbortEval: '15s' }],
    servicenow_latency: ['p(95)<2000', 'p(99)<3000'],
    dropped_iterations: ['count==0'],
  },
  noConnectionReuse: false,
  summaryTrendStats: ['avg', 'min', 'med', 'p(50)', 'p(95)', 'p(99)', 'max'],
  userAgent: 'ARIA-approved-servicenow-load-test/1.0',
};

export function setup() {
  const clientId = required('SERVICENOW_CLIENT_ID');
  const clientSecret = required('SERVICENOW_CLIENT_SECRET');
  const response = http.post(tokenEndpoint, { grant_type: 'client_credentials' }, {
    auth: 'basic',
    headers: { Authorization: `Basic ${encoding.b64encode(`${clientId}:${clientSecret}`)}` },
    redirects: 0,
    tags: { operation: 'oauth_setup' },
  });
  if (response.status !== 200) fail(`OAuth setup failed with HTTP ${response.status}`);
  const token = response.json('access_token');
  if (!token) fail('OAuth setup returned no access token');
  return { token };
}

export default function (data) {
  const separator = endpoint.includes('?') ? '&' : '?';
  const url = `${endpoint}${separator}upn=${encodeURIComponent(syntheticUpn)}&q=${encodeURIComponent(syntheticQuery)}`;
  const response = http.get(url, {
    headers: { Authorization: `Bearer ${data.token}` },
    redirects: 0,
    timeout: '3s',
    tags: { operation: 'ai_search' },
  });

  latency.add(response.timings.duration);
  throttles.add(response.status === 429 ? 1 : 0);
  timeouts.add(response.error_code === 1050 ? 1 : 0);
  const succeeded = check(response, { 'search returned 2xx': (value) => value.status >= 200 && value.status < 300 });
  errorRate.add(!succeeded);
}

export function handleSummary(data) {
  const summary = {
    generatedAt: new Date().toISOString(),
    achievedRps: data.metrics.http_reqs?.values?.rate,
    latency: {
      p50: data.metrics.servicenow_latency?.values?.['p(50)'],
      p95: data.metrics.servicenow_latency?.values?.['p(95)'],
      p99: data.metrics.servicenow_latency?.values?.['p(99)'],
    },
    errors: data.metrics.servicenow_errors?.values?.rate,
    throttles: data.metrics.servicenow_429s?.values?.count,
    timeouts: data.metrics.servicenow_timeouts?.values?.count,
    droppedIterations: data.metrics.dropped_iterations?.values?.count,
  };
  return { 'servicenow-load-summary.json': JSON.stringify(summary, null, 2) };
}
