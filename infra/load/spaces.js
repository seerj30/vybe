// VYBE real-time spaces load test (k6): many avatars in the plaza hub (docs/design/p2-spaces-protocol.md), each with
// its own guest, ticket and WebSocket, speaking the SignalR MessagePack protocol like the web client. Run it at the
// same time as infra/load/street-hoops.js to check that the HTTP SLO still holds (docs/p2/02-real-time-spaces.md).
// How to run: docs/runbooks/load-test.md. Local targets only: the script stops for any other host.
import http from 'k6/http';
import { check, fail } from 'k6';
import { WebSocket } from 'k6/experimental/websockets';
import { Counter, Trend } from 'k6/metrics';

const BASE_URL = (__ENV.BASE_URL || 'http://localhost:8080').replace(/\/$/, '');
const BASE_URL_PATTERN = /^https?:\/\/(\[[0-9A-Fa-f:.]+\]|[A-Za-z0-9.-]+)(?::\d{1,5})?(?:\/[^?#@]*)?$/;
const LOCAL_HOSTS = ['localhost', '127.0.0.1', '[::1]', 'host.docker.internal', 'api'];
const COOKIE = '__Host-vybe_guest';
const CSRF = { 'X-Requested-With': 'vybe-web' };
// Avatars per step and the time each step holds (protocol doc: 100, 250, 500, 1,000 virtual players).
const STEPS = (__ENV.STEPS || '100,250,500,1000').split(',').map(Number);
const STEP = __ENV.STEP || '2m';
const RAMP = __ENV.RAMP || '30s';
// Each avatar keeps its socket this long, then reconnects with a new guest (also exercises join and leave).
const SESSION_MS = Number(__ENV.SESSION_MS || '120000');

const snapshotGap = new Trend('spaces_snapshot_gap', true);
const joinTime = new Trend('spaces_join_time', true);
const snapshots = new Counter('spaces_snapshots');
const kicks = new Counter('spaces_kicks');
const drops = new Counter('spaces_dropped_connections');
const serverErrors = new Counter('spaces_errors');

export const options = {
  scenarios: {
    avatars: {
      executor: 'ramping-vus',
      startVUs: 0,
      stages: STEPS.flatMap((target) => [{ duration: RAMP, target }, { duration: STEP, target }]).concat([{ duration: '20s', target: 0 }]),
      gracefulRampDown: '10s',
    },
  },
  thresholds: {
    // Pass rule of docs/p2/02-real-time-spaces.md: snapshot interval p95 < 150 ms (10 Hz while avatars move).
    spaces_snapshot_gap: ['p(95)<150'],
    spaces_join_time: ['p(95)<1000'],
    spaces_kicks: ['count==0'],
    spaces_dropped_connections: ['count==0'],
    checks: ['rate>0.99'],
  },
};

export function setup() {
  const match = BASE_URL_PATTERN.exec(BASE_URL);
  if (!match) {
    fail('BASE_URL must be http(s)://host[:port][/path] without user info, query or fragment');
  }
  const host = match[1].toLowerCase();
  if (!LOCAL_HOSTS.includes(host) && __ENV.VYBE_LOAD_TARGET_CONFIRMED !== host) {
    fail(`refusing to load ${host}: only local targets (see docs/runbooks/load-test.md)`);
  }
}

// ---------------------------------------------------------------- minimal MessagePack (SignalR framing)

function encodeInt(n, out) {
  if (n >= 0 && n < 128) out.push(n);
  else if (n < 0 && n >= -32) out.push(0x100 + n);
  else if (n >= -128 && n < 128) out.push(0xd0, n & 0xff);
  else throw new Error(`int ${n} out of range`);
}

function encodeStr(s, out) {
  if (s.length > 31) throw new Error('string too long');
  out.push(0xa0 | s.length);
  for (let i = 0; i < s.length; i++) out.push(s.charCodeAt(i));
}

/** [1, {}, invocationId|nil, target, args] with a varint length prefix. Args: small ints and short ASCII strings. */
function invocation(target, args, id) {
  const body = [0x95, 0x01, 0x80];
  if (id === undefined) body.push(0xc0);
  else encodeStr(id, body);
  encodeStr(target, body);
  body.push(0x90 | args.length);
  for (const a of args) {
    if (typeof a === 'number') encodeInt(a, body);
    else encodeStr(a, body);
  }
  return frame(body);
}

function frame(body) {
  const out = [];
  let n = body.length;
  do {
    let b = n & 0x7f;
    n >>= 7;
    if (n > 0) b |= 0x80;
    out.push(b);
  } while (n > 0);
  return new Uint8Array(out.concat(body)).buffer;
}

const PING = frame([0x91, 0x06]);

/** Splits a binary WebSocket message into SignalR messages and reads type and target. */
function readMessages(buffer) {
  const bytes = new Uint8Array(buffer);
  const messages = [];
  let at = 0;
  while (at < bytes.length) {
    let length = 0;
    let shift = 0;
    let b;
    do {
      b = bytes[at++];
      length |= (b & 0x7f) << shift;
      shift += 7;
    } while (b & 0x80);
    messages.push(readHeader(bytes, at, at + length));
    at += length;
  }
  return messages;
}

function readHeader(bytes, start, end) {
  let at = start;
  const head = bytes[at++];
  if ((head & 0xf0) !== 0x90) return { type: -1 };
  const type = bytes[at++];
  if (type !== 1) return { type, raw: bytes.subarray(start, end) };
  if ((bytes[at] & 0xf0) !== 0x80 || bytes[at] !== 0x80) return { type };
  at++; // empty headers map (the server sends none)
  if (bytes[at] === 0xc0) at++;
  else at += 1 + (bytes[at] & 0x1f);
  const len = bytes[at++] & 0x1f;
  let target = '';
  for (let i = 0; i < len; i++) target += String.fromCharCode(bytes[at + i]);
  return { type, target };
}

// ---------------------------------------------------------------- one avatar

function guest() {
  const res = http.post(`${BASE_URL}/api/v1/guests`, JSON.stringify({ birthYear: 1990, birthMonth: 6 }), {
    headers: Object.assign({ 'Content-Type': 'application/json' }, CSRF),
    tags: { kind: 'write' },
  });
  check(res, { 'guest 201': (r) => r.status === 201 });
  const cookie = res.cookies[COOKIE];
  return cookie && cookie.length > 0 ? cookie[0].value : null;
}

function ticket(token) {
  const res = http.post(`${BASE_URL}/api/v1/spaces/tickets`, null, {
    headers: Object.assign({ Cookie: `${COOKIE}=${token}` }, CSRF),
    tags: { kind: 'write' },
  });
  check(res, { 'ticket 201': (r) => r.status === 201 });
  return res.status === 201 ? res.json('ticket') : null;
}

export default function () {
  const token = guest();
  const hubTicket = token && ticket(token);
  if (!hubTicket) return;

  const wsUrl = `${BASE_URL.replace(/^http/, 'ws')}/api/v1/spaces/hub?access_token=${hubTicket}`;
  const ws = new WebSocket(wsUrl);
  ws.binaryType = 'arraybuffer';
  let joined = false;
  let closedByUs = false;
  let lastSnapshot = 0;
  let joinStart = 0;
  let dx = 0;
  let dz = 127;
  const timers = [];

  ws.onopen = () => {
    ws.send('{"protocol":"messagepack","version":1}\u001e');
  };

  ws.onmessage = (event) => {
    if (typeof event.data === 'string' || !joined && joinStart === 0) {
      // Handshake response ({}\u001e), text or binary. Then join and start walking.
      joinStart = Date.now();
      ws.send(invocation('Join', [], '1'));
      return;
    }
    for (const message of readMessages(event.data)) {
      if (message.type === 3 && !joined) {
        joined = true;
        joinTime.add(Date.now() - joinStart);
        startWalking();
      } else if (message.type === 1 && message.target === 'snapshot') {
        const now = Date.now();
        if (lastSnapshot > 0) snapshotGap.add(now - lastSnapshot);
        lastSnapshot = now;
        snapshots.add(1);
      } else if (message.type === 1 && message.target === 'kicked') {
        kicks.add(1);
      } else if (message.type === 1 && message.target === 'error') {
        serverErrors.add(1);
      }
    }
  };

  function startWalking() {
    // Move at 10 Hz like a walking player; a new direction every 2 s; a quick-chat every ~20 s; ping every 15 s.
    timers.push(setInterval(() => ws.send(invocation('Move', [dx, dz])), 100));
    timers.push(setInterval(() => {
      const angle = Math.random() * 2 * Math.PI;
      dx = Math.round(127 * Math.sin(angle));
      dz = Math.round(127 * Math.cos(angle));
    }, 2000));
    timers.push(setInterval(() => ws.send(invocation('QuickChat', ['hi'])), 20000 + Math.floor(Math.random() * 5000)));
    timers.push(setInterval(() => ws.send(PING), 15000));
    timers.push(setTimeout(() => {
      closedByUs = true;
      ws.close();
    }, SESSION_MS));
  }

  ws.onclose = () => {
    timers.forEach((t) => clearInterval(t));
    check(joined, { 'joined the plaza': (j) => j === true });
    if (!closedByUs) drops.add(1);
  };

  ws.onerror = () => {
    if (!closedByUs) drops.add(1);
  };
}
