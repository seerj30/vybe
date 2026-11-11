// VYBE API load test (k6): guest creation, Street Hoops runs (create, start, submit), purchases and challenge
// acceptance (spec §13) at about 20 requests/s in total, the closed-beta target of docs/decisions/07-hosting.md.
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
// Part of the target rate (0 < RATE_SCALE <= 1), for a short check on a shared target. 1 = about 20 requests/s.
const RATE_SCALE = Number(__ENV.RATE_SCALE || '1');
if (!(RATE_SCALE > 0 && RATE_SCALE <= 1)) {
  throw new Error('RATE_SCALE must be a number greater than 0 and not more than 1');
}
const per = (ms) => `${Math.round(ms / RATE_SCALE)}ms`;
const LOCAL_HOSTS = ['localhost', '127.0.0.1', '[::1]', 'host.docker.internal', 'api'];
const COOKIE = '__Host-vybe_guest';
const CSRF = { 'X-Requested-With': 'vybe-web' };
// A guest does at most this many writes, far below the per-player limit (default 120 per minute).
const WRITES_PER_GUEST = 40;

export const options = {
  scenarios: {
    // ~3 requests per iteration: create a guest, read the catalog, read the profile → ~6 rps.
    guests: { executor: 'constant-arrival-rate', exec: 'guestJourney', rate: 2, timeUnit: per(1000), duration: DURATION, preAllocatedVUs: 10, maxVUs: 40 },
    // 3 writes per iteration, spread over a 60 s run like a real player → ~3 rps, ~65 concurrent runs.
    runs: { executor: 'constant-arrival-rate', exec: 'playRun', rate: 1, timeUnit: per(1000), duration: DURATION, preAllocatedVUs: 80, maxVUs: 150 },
    // 2 requests per iteration: purchase (200 or 409 insufficient_funds) and a wallet read → ~8 rps.
    purchases: { executor: 'constant-arrival-rate', exec: 'purchase', rate: 4, timeUnit: per(1000), duration: DURATION, preAllocatedVUs: 20, maxVUs: 60 },
    // Two players, two full-length runs (2 × 58.5 s): 12 requests in ~2 minutes per iteration. One iteration every
    // 4 s → ~3 rps, ~30 iterations open at the same time. gracefulStop lets the last iterations finish their runs.
    challenges: {
      executor: 'constant-arrival-rate', exec: 'challengeAcceptance', rate: 1, timeUnit: per(4000), duration: DURATION,
      preAllocatedVUs: 40, maxVUs: 80, gracefulStop: '150s',
    },
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
  // An adult age-gate answer (spec §17 decision 1); the API keeps only the result.
  // An empty cookie jar: over HTTPS the VU jar keeps the previous guest's __Host- cookie and would send it,
  // so the API would not create a new guest (a local HTTP target never stores a Secure cookie).
  const res = http.post(`${BASE_URL}/api/v1/guests`, JSON.stringify({ birthYear: 1990, birthMonth: 6 }), {
    headers: Object.assign({ 'Content-Type': 'application/json' }, CSRF),
    tags: { kind: 'write', name: 'createGuest' },
    jar: new http.CookieJar(),
  });
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

// A reward-eligible run (spec v1.4 §7): verified, at least minShotsForRewards shots, and submitted at least
// minRunMsForRewards after the start. With challengeId, it is the recipient run of an accepted challenge.
function fullLengthRun(config, challengeId, prefix) {
  const created = write('POST', '/api/v1/games/street-hoops/sessions', challengeId ? { challengeId } : {}, `${prefix}CreateSession`);
  if (!check(created, { [`${prefix} session 201`]: (r) => r.status === 201 })) {
    return null;
  }
  const sessionId = created.json('sessionId');
  const rulesetId = created.json('rulesetId');
  const started = write('POST', `/api/v1/games/sessions/${sessionId}/start`, null, `${prefix}StartSession`);
  if (!check(started, { [`${prefix} start 200`]: (r) => r.status === 200 })) {
    return null;
  }

  // Shots until 1.5 s before minRunMsForRewards; the submit leaves 0.5 s after it.
  const events = [];
  for (let t = randomIntBetween(300, 1500); t < config.minRunMsForRewards - 1500 && events.length < config.maxEvents; t += randomIntBetween(config.minGapMs, 2 * config.minGapMs)) {
    events.push({ tMs: t, aimX: randomIntBetween(config.xMin, config.xMax), power: randomIntBetween(config.pMin, config.pMax) });
  }
  sleep(config.minRunMsForRewards / 1000 + 0.5);

  const submitted = write('POST', `/api/v1/games/sessions/${sessionId}/submit`, { rulesetId, events, clientReportedScore: 0 }, `${prefix}Submit`, { 'Idempotency-Key': crypto.randomUUID() });
  return check(submitted, { [`${prefix} submit verified`]: (r) => r.status === 200 && r.json('status') === 'verified' }) ? sessionId : null;
}

// Spec §13 "challenge acceptance", J2: a creator plays a full-length run and creates a challenge; a second player
// opens the invite, accepts it and plays the recipient run; the challenge is then completed.
export function challengeAcceptance(data) {
  guest = null;
  if (!createGuest()) {
    return;
  }
  const creatorSessionId = fullLengthRun(data.config, null, 'creator');
  if (creatorSessionId === null) {
    return;
  }
  const created = write('POST', '/api/v1/challenges', { creatorSessionId }, 'createChallenge', { 'Idempotency-Key': crypto.randomUUID() });
  if (!check(created, { 'challenge 201': (r) => r.status === 201 })) {
    return;
  }
  const challengeId = created.json('challengeId');
  const token = /\/challenge\/([A-Za-z0-9_-]+)$/.exec(created.json('shareUrl'))[1];

  guest = null;
  if (!createGuest()) {
    return;
  }
  check(read(`/api/v1/invites/${token}`, 'invitePreview'), { 'invite 200': (r) => r.status === 200 });
  const accepted = write('POST', `/api/v1/invites/${token}/accept`, null, 'acceptInvite', { 'Idempotency-Key': crypto.randomUUID() });
  if (!check(accepted, { 'accept 200': (r) => r.status === 200 && r.json('status') === 'accepted' })) {
    return;
  }
  if (fullLengthRun(data.config, challengeId, 'recipient') === null) {
    return;
  }
  check(read(`/api/v1/challenges/${challengeId}`, 'challenge'), { 'challenge completed': (r) => r.status === 200 && r.json('status') === 'completed' });
}

export function purchase(data) {
  if (!ensureGuest()) {
    return;
  }
  const res = write('POST', '/api/v1/purchases', { itemId: data.itemId }, 'purchase', { 'Idempotency-Key': crypto.randomUUID() });
  check(res, { 'purchase 200 or 409': (r) => r.status === 200 || r.status === 409 });
  check(read('/api/v1/me', 'me'), { 'me 200': (r) => r.status === 200 });
}
