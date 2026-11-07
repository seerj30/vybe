// VYBE API load test (k6): guest creation, Street Hoops runs (create, start, submit) and purchases at about
// 20 requests/s in total, the closed-beta target of docs/decisions/07-hosting.md.
// How to run: docs/runbooks/load-test.md. Local targets only: the script stops for any other host.
import http from 'k6/http';
import { check, fail, sleep } from 'k6';

// No remote imports: the script runs only what is in this repository.
function randomIntBetween(min, max) {
  return Math.floor(Math.random() * (max - min + 1)) + min;
}

const BASE_URL = (__ENV.BASE_URL || 'http://localhost:8080').replace(/\/$/, '');
// scheme://host[:port][/path] only: no user info ("localhost@evil.example"), query or fragment.
const BASE_URL_PATTERN = /^https?:\/\/(\[[0-9A-Fa-f:.]+\]|[A-Za-z0-9.-]+)(?::\d{1,5})?(?:\/[^?#@]*)?$/;
const DURATION = __ENV.DURATION || '5m';
const LOCAL_HOSTS = ['localhost', '127.0.0.1', '[::1]', 'host.docker.internal', 'api'];
const COOKIE = '__Host-vybe_guest';
const CSRF = { 'X-Requested-With': 'vybe-web' };
// A guest does at most this many writes, far below the per-player limit (default 120 per minute).
const WRITES_PER_GUEST = 40;

export const options = {
  scenarios: {
    // ~3 requests per iteration: create a guest, read the catalog, read the profile → ~6 rps.
    guests: { executor: 'constant-arrival-rate', exec: 'guestJourney', rate: 2, timeUnit: '1s', duration: DURATION, preAllocatedVUs: 10, maxVUs: 40 },
    // 3 writes per iteration, spread over a 60 s run like a real player → ~3 rps, ~65 concurrent runs.
    runs: { executor: 'constant-arrival-rate', exec: 'playRun', rate: 1, timeUnit: '1s', duration: DURATION, preAllocatedVUs: 80, maxVUs: 150 },
    // 2 requests per iteration: purchase (200 or 409 insufficient_funds) and a wallet read → ~10 rps.
    purchases: { executor: 'constant-arrival-rate', exec: 'purchase', rate: 5, timeUnit: '1s', duration: DURATION, preAllocatedVUs: 20, maxVUs: 60 },
  },
  thresholds: {
    // Spec §13 beta SLO: standard reads p95 < 300 ms. Writes get a wider budget until a baseline exists.
    'http_req_duration{kind:read}': ['p(95)<300'],
    'http_req_duration{kind:write}': ['p(95)<800'],
    http_req_failed: ['rate<0.01'],
    checks: ['rate>0.99'],
  },
};

// 409 insufficient_funds is a normal outcome of a purchase by a new guest.
http.setResponseCallback(http.expectedStatuses({ min: 200, max: 299 }, 409));

export function setup() {
  const match = BASE_URL_PATTERN.exec(BASE_URL);
  if (!match) {
    fail('BASE_URL must be http(s)://host[:port][/path] without user info, query or fragment');
  }
  const host = match[1].toLowerCase();
  if (!LOCAL_HOSTS.includes(host) && __ENV.VYBE_LOAD_TARGET_CONFIRMED !== host) {
    fail(`refusing to load ${host}: only local targets (see docs/runbooks/load-test.md)`);
  }

  const ruleset = http.get(`${BASE_URL}/api/v1/games/rulesets/street-hoops-v1`, { tags: { kind: 'read' } }).json();
  const catalog = http.get(`${BASE_URL}/api/v1/catalog/items`, { tags: { kind: 'read' } }).json();
  const item = catalog.items.find((i) => i.price > 0 && i.minLevel === 1);
  if (!ruleset.config || !item) {
    fail('the API has no seeded content: run the seed step first');
  }
  return { config: ruleset.config, itemId: item.id };
}

// One guest per VU, replaced after WRITES_PER_GUEST writes.
let guest = null;

function headers(extra) {
  return Object.assign({ 'Content-Type': 'application/json', Cookie: `${COOKIE}=${guest.token}` }, CSRF, extra || {});
}

function createGuest() {
  const res = http.post(`${BASE_URL}/api/v1/guests`, null, { headers: CSRF, tags: { kind: 'write', name: 'createGuest' } });
  check(res, { 'guest 201': (r) => r.status === 201 });
  const cookie = res.cookies[COOKIE];
  if (res.status !== 201 || !cookie || cookie.length === 0) {
    return false;
  }
  guest = { token: cookie[0].value, writes: 1 };
  return true;
}

function ensureGuest() {
  if (guest === null || guest.writes >= WRITES_PER_GUEST) {
    return createGuest();
  }
  return true;
}

function write(method, path, body, name, extraHeaders) {
  guest.writes += 1;
  return http.request(method, `${BASE_URL}${path}`, body === null ? null : JSON.stringify(body), {
    headers: headers(extraHeaders),
    tags: { kind: 'write', name },
  });
}

function read(path, name) {
  return http.get(`${BASE_URL}${path}`, { headers: headers(), tags: { kind: 'read', name } });
}

export function guestJourney() {
  guest = null;
  if (!createGuest()) {
    return;
  }
  check(read('/api/v1/catalog/items', 'catalog'), { 'catalog 200': (r) => r.status === 200 });
  check(read('/api/v1/me', 'me'), { 'me 200': (r) => r.status === 200 });
}

export function playRun(data) {
  if (!ensureGuest()) {
    return;
  }
  const created = write('POST', '/api/v1/games/street-hoops/sessions', {}, 'createSession');
  if (!check(created, { 'session 201': (r) => r.status === 201 })) {
    return;
  }
  const sessionId = created.json('sessionId');
  const rulesetId = created.json('rulesetId');
  const started = write('POST', `/api/v1/games/sessions/${sessionId}/start`, null, 'startSession');
  if (!check(started, { 'start 200': (r) => r.status === 200 })) {
    return;
  }

  // Shots over the whole run, at least minGapMs apart, with random aim: the server simulates every shot.
  const config = data.config;
  const events = [];
  for (let t = randomIntBetween(300, 1500); t < config.durationMs && events.length < config.maxEvents; t += randomIntBetween(config.minGapMs, 2 * config.minGapMs)) {
    events.push({ tMs: t, aimX: randomIntBetween(config.xMin, config.xMax), power: randomIntBetween(config.pMin, config.pMax) });
  }
  sleep(events[events.length - 1].tMs / 1000 + 0.2);

  const submitted = write('POST', `/api/v1/games/sessions/${sessionId}/submit`, { rulesetId, events, clientReportedScore: 0 }, 'submit', { 'Idempotency-Key': crypto.randomUUID() });
  check(submitted, { 'submit verified': (r) => r.status === 200 && r.json('status') === 'verified' });
}

export function purchase(data) {
  if (!ensureGuest()) {
    return;
  }
  const res = write('POST', '/api/v1/purchases', { itemId: data.itemId }, 'purchase', { 'Idempotency-Key': crypto.randomUUID() });
  check(res, { 'purchase 200 or 409': (r) => r.status === 200 || r.status === 409 });
  check(read('/api/v1/me', 'me'), { 'me 200': (r) => r.status === 200 });
}
