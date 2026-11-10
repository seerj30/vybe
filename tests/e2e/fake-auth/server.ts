/**
 * Fake Supabase Auth for the browser tests only (never for a real deployment). It speaks the subset of
 * the GoTrue API that supabase-js and the VYBE API use: password sign-in, refresh, sign-up with the
 * PKCE email confirmation (`/verify` redirects with `?code=`, `grant_type=pkce` checks the
 * `code_verifier`), sign-out, `GET /auth/v1/user`, the issuer metadata and the JWKS. Tokens are ES256
 * JWTs with the claims of a real Supabase access token (`amr`, `app_metadata`, `is_anonymous`).
 * Sign-up addresses that contain "+unconfirmed" act like "Confirm email" on.
 *
 * It answers only to `localhost:<port>` (Host check) and allows CORS only for the web app origin.
 * Test-only endpoints under `/__test` need the `X-Test-Secret` header with `E2E_AUTH_TEST_SECRET` (no CORS):
 * - `POST /__test/users` `{ email, password, role?, confirmed?, amrAgeSeconds? }`
 * - `PATCH /__test/users/{email}` `{ role?, amrAgeSeconds?, userFailures? }`
 * - `GET /__test/users/{email}/confirmation` → `{ url }`, the link of the confirmation email
 * `amrAgeSeconds` moves the sign-in time (`amr[].timestamp`) into the past; `userFailures` makes the
 * next N calls of `GET /auth/v1/user` answer 503 (Auth outage).
 *
 * Run: `E2E_AUTH_TEST_SECRET=<random> node tests/e2e/fake-auth/server.ts` (port `E2E_AUTH_PORT`, default 5290;
 * web origin port `E2E_WEB_PORT`, default 4321).
 */
import { createHash, generateKeyPairSync, randomBytes, randomUUID, sign, verify } from "node:crypto";
import { createServer, type IncomingMessage, type ServerResponse } from "node:http";
import { AUTH_ANON_KEY, AUTH_ISSUER, AUTH_PORT, AUTH_SERVICE_ROLE_KEY, AUTH_URL, WEB_ORIGIN, testSecret } from "./config.ts";

const TEST_SECRET = testSecret(); // the server does not start without it
/** Host names this server answers to (no DNS rebinding through another name). */
const HOSTS = new Set([`localhost:${AUTH_PORT}`, `127.0.0.1:${AUTH_PORT}`]);

interface User {
  id: string;
  email: string;
  password: string;
  role: string | null;
  confirmed: boolean;
  amrAgeSeconds: number;
  userFailures: number;
  createdAt: string;
  /** Sign-up waiting for the email link: the link token and the PKCE challenge of the tab that signed up. */
  pending: { token: string; challenge: string | null; method: string; redirectTo: string } | null;
}

interface AuthCode {
  userId: string;
  challenge: string;
  method: string;
}

interface Session {
  id: string;
  userId: string;
  signedInAt: number;
}

const TOKEN_SECONDS = 3600;
const { privateKey, publicKey } = generateKeyPairSync("ec", { namedCurve: "P-256" });
const kid = createHash("sha256").update(publicKey.export({ format: "der", type: "spki" })).digest("base64url").slice(0, 16);
const jwk = { ...publicKey.export({ format: "jwk" }), kid, alg: "ES256", use: "sig" };

const users = new Map<string, User>(); // by lowercase email
const sessions = new Map<string, Session>(); // by session id
const refreshTokens = new Map<string, string>(); // refresh token → session id
const authCodes = new Map<string, AuthCode>(); // one-time PKCE codes
const MAX_BODY = 64 * 1024;

class HttpError extends Error {
  readonly status: number;
  readonly errorCode: string;
  readonly headers: Record<string, string>;

  constructor(status: number, errorCode: string, message: string, headers: Record<string, string> = {}) {
    super(message);
    this.status = status;
    this.errorCode = errorCode;
    this.headers = headers;
  }
}

const now = (): number => Math.floor(Date.now() / 1000);

function userById(id: string): User | undefined {
  return [...users.values()].find((u) => u.id === id);
}

function userJson(user: User): Record<string, unknown> {
  const confirmedAt = user.confirmed ? user.createdAt : null;
  return {
    id: user.id,
    aud: "authenticated",
    role: "authenticated",
    email: user.email,
    email_confirmed_at: confirmedAt,
    confirmed_at: confirmedAt,
    phone: "",
    app_metadata: appMetadata(user),
    user_metadata: { email: user.email, email_verified: user.confirmed },
    identities: [],
    created_at: user.createdAt,
    updated_at: user.createdAt,
    is_anonymous: false,
  };
}

function appMetadata(user: User): Record<string, unknown> {
  return { provider: "email", providers: ["email"], ...(user.role === null ? {} : { vybe_role: user.role }) };
}

function accessToken(user: User, session: Session): string {
  const iat = now();
  const header = { alg: "ES256", kid, typ: "JWT" };
  const payload = {
    iss: AUTH_ISSUER,
    sub: user.id,
    aud: "authenticated",
    exp: iat + TOKEN_SECONDS,
    iat,
    email: user.email,
    phone: "",
    app_metadata: appMetadata(user),
    user_metadata: { email: user.email, email_verified: user.confirmed },
    role: "authenticated",
    aal: "aal1",
    amr: [{ method: "password", timestamp: session.signedInAt }],
    session_id: session.id,
    is_anonymous: false,
  };
  const data = `${b64(header)}.${b64(payload)}`;
  const signature = sign("sha256", Buffer.from(data), { key: privateKey, dsaEncoding: "ieee-p1363" });
  return `${data}.${signature.toString("base64url")}`;
}

function b64(value: unknown): string {
  return Buffer.from(JSON.stringify(value)).toString("base64url");
}

/** The session of a valid access token, or a 403 like Supabase (`session_not_found`, `bad_jwt`). */
function sessionOf(request: IncomingMessage): { user: User; session: Session } {
  const token = /^Bearer (.+)$/i.exec(request.headers.authorization ?? "")?.[1];
  const parts = token?.split(".") ?? [];
  if (parts.length !== 3) throw new HttpError(401, "no_authorization", "This endpoint requires a valid Bearer token");
  const [header, payload, signature] = parts as [string, string, string];
  const valid = verify("sha256", Buffer.from(`${header}.${payload}`), { key: publicKey, dsaEncoding: "ieee-p1363" }, Buffer.from(signature, "base64url"));
  const claims = valid ? (JSON.parse(Buffer.from(payload, "base64url").toString()) as { sub: string; exp: number; session_id: string }) : null;
  if (claims === null || claims.exp < now()) throw new HttpError(403, "bad_jwt", "invalid JWT");
  const session = sessions.get(claims.session_id);
  const user = userById(claims.sub);
  if (session === undefined || user === undefined) throw new HttpError(403, "session_not_found", "Session not found");
  return { user, session };
}

function issue(user: User, session: Session): Record<string, unknown> {
  const refreshToken = randomBytes(16).toString("hex");
  refreshTokens.set(refreshToken, session.id);
  return {
    access_token: accessToken(user, session),
    token_type: "bearer",
    expires_in: TOKEN_SECONDS,
    expires_at: now() + TOKEN_SECONDS,
    refresh_token: refreshToken,
    user: userJson(user),
  };
}

function signIn(user: User): Record<string, unknown> {
  const session = { id: randomUUID(), userId: user.id, signedInAt: now() - user.amrAgeSeconds };
  sessions.set(session.id, session);
  return issue(user, session);
}

function newUser(body: Record<string, unknown>, defaults: { confirmed: boolean }): User {
  const email = typeof body["email"] === "string" ? body["email"].trim().toLowerCase() : "";
  const password = typeof body["password"] === "string" ? body["password"] : "";
  if (!email.includes("@")) throw new HttpError(400, "validation_failed", "Unable to validate email address: invalid format");
  if (password.length < 6) throw new HttpError(422, "weak_password", "Password should be at least 6 characters.");
  if (users.has(email)) throw new HttpError(422, "user_already_exists", "User already registered");
  const user: User = {
    id: randomUUID(),
    email,
    password,
    role: typeof body["role"] === "string" ? body["role"] : null,
    confirmed: typeof body["confirmed"] === "boolean" ? body["confirmed"] : defaults.confirmed,
    amrAgeSeconds: typeof body["amrAgeSeconds"] === "number" ? body["amrAgeSeconds"] : 0,
    userFailures: 0,
    createdAt: new Date().toISOString(),
    pending: null,
  };
  users.set(email, user);
  return user;
}

type Answer = [number, unknown] | [number, unknown, Record<string, string>];

async function route(request: IncomingMessage, url: URL): Promise<Answer> {
  const path = url.pathname;
  const method = request.method ?? "GET";

  if (method === "GET" && path === "/runtime-config.json") {
    // The VYBE web app's runtime config (apps/web/src/app/auth/runtime-config.ts); the dev proxy serves it.
    return [200, { supabaseUrl: AUTH_URL, supabaseAnonKey: AUTH_ANON_KEY }];
  }
  if (method === "GET" && path === "/auth/v1/.well-known/openid-configuration") {
    return [200, { issuer: AUTH_ISSUER, jwks_uri: `${AUTH_ISSUER}/.well-known/jwks.json`, id_token_signing_alg_values_supported: ["ES256"] }];
  }
  if (method === "GET" && path === "/auth/v1/.well-known/jwks.json") return [200, { keys: [jwk] }];

  if (path.startsWith("/__test/")) {
    if (request.headers["x-test-secret"] !== TEST_SECRET) throw new HttpError(403, "forbidden", "Test endpoints need X-Test-Secret");
    return testRoute(method, path, await readJson(request));
  }

  if (method === "GET" && path === "/auth/v1/verify") {
    // The link of the confirmation email: confirms the user, then redirects like Supabase does.
    const user = [...users.values()].find((u) => u.pending !== null && u.pending.token === url.searchParams.get("token"));
    if (user === undefined || user.pending === null) throw new HttpError(403, "otp_expired", "Email link is invalid or has expired");
    const { challenge, method: challengeMethod, redirectTo } = user.pending;
    user.confirmed = true;
    user.pending = null;
    if (challenge === null) {
      // Implicit flow: the session goes in the URL fragment.
      const session = signIn(user) as Record<string, string | number>;
      const fragment = new URLSearchParams(Object.entries(session).filter(([k]) => k !== "user").map(([k, v]) => [k, String(v)]));
      return [303, null, { Location: `${redirectTo}#${fragment.toString()}&type=signup` }];
    }
    const code = randomUUID();
    authCodes.set(code, { userId: user.id, challenge, method: challengeMethod });
    const target = new URL(redirectTo);
    target.searchParams.set("code", code);
    return [303, null, { Location: target.toString() }];
  }

  const adminUser = /^\/auth\/v1\/admin\/users\/([^/]+)$/.exec(path);
  if (method === "DELETE" && adminUser !== null) {
    // Auth admin API (hard delete), as the VYBE API calls it in J4: only with the service-role key.
    if (request.headers["apikey"] !== AUTH_SERVICE_ROLE_KEY || request.headers["authorization"] !== `Bearer ${AUTH_SERVICE_ROLE_KEY}`) {
      throw new HttpError(401, "no_authorization", "This endpoint requires a service-role key");
    }
    const user = userById(adminUser[1] ?? "");
    if (user === undefined) throw new HttpError(404, "user_not_found", "User not found");
    users.delete(user.email.toLowerCase());
    for (const [id, s] of sessions) if (s.userId === user.id) sessions.delete(id);
    return [200, {}];
  }

  if (path.startsWith("/auth/v1/") && request.headers["apikey"] !== AUTH_ANON_KEY) {
    throw new HttpError(401, "no_api_key", "No API key found in request");
  }

  if (method === "POST" && path === "/auth/v1/token") {
    const body = await readJson(request);
    const grant = url.searchParams.get("grant_type");
    if (grant === "password") {
      const user = users.get(String(body["email"] ?? "").trim().toLowerCase());
      if (user === undefined || user.password !== body["password"]) throw new HttpError(400, "invalid_credentials", "Invalid login credentials");
      if (!user.confirmed) throw new HttpError(400, "email_not_confirmed", "Email not confirmed");
      return [200, signIn(user)];
    }
    if (grant === "refresh_token") {
      const token = String(body["refresh_token"] ?? "");
      const session = sessions.get(refreshTokens.get(token) ?? "");
      const user = session === undefined ? undefined : userById(session.userId);
      if (session === undefined || user === undefined) throw new HttpError(400, "refresh_token_not_found", "Invalid Refresh Token: Refresh Token Not Found");
      refreshTokens.delete(token); // rotation; a refresh keeps the original sign-in time in `amr`
      return [200, issue(user, session)];
    }
    if (grant === "pkce") {
      const code = authCodes.get(String(body["auth_code"] ?? ""));
      authCodes.delete(String(body["auth_code"] ?? "")); // one use only
      const verifier = String(body["code_verifier"] ?? "");
      const expected = code?.method === "plain" ? verifier : createHash("sha256").update(verifier).digest("base64url");
      const user = code === undefined ? undefined : userById(code.userId);
      if (code === undefined || user === undefined || verifier === "" || expected !== code.challenge) {
        throw new HttpError(400, "bad_code_verifier", "code challenge does not match previously saved code verifier");
      }
      return [200, signIn(user)];
    }
    throw new HttpError(400, "unsupported_grant_type", "unsupported_grant_type");
  }

  if (method === "POST" && path === "/auth/v1/signup") {
    // Addresses with "+unconfirmed" behave like "Confirm email" on: no session until the email is confirmed.
    const body = await readJson(request);
    const user = newUser({ email: body["email"], password: body["password"] }, { confirmed: !String(body["email"]).includes("+unconfirmed") });
    if (user.confirmed) return [200, signIn(user)];
    user.pending = {
      token: randomBytes(16).toString("hex"),
      challenge: typeof body["code_challenge"] === "string" ? body["code_challenge"] : null,
      method: typeof body["code_challenge_method"] === "string" ? body["code_challenge_method"] : "s256",
      redirectTo: url.searchParams.get("redirect_to") ?? AUTH_URL,
    };
    return [200, userJson(user)];
  }

  if (method === "GET" && path === "/auth/v1/user") {
    const { user } = sessionOf(request);
    if (user.userFailures > 0) {
      user.userFailures--;
      throw new HttpError(503, "service_unavailable", "Auth is unavailable", { "Retry-After": "1" });
    }
    return [200, userJson(user)];
  }

  if (method === "POST" && path === "/auth/v1/logout") {
    const { user, session } = sessionOf(request);
    const scope = url.searchParams.get("scope") ?? "global";
    for (const [id, s] of sessions) {
      if (scope === "global" ? s.userId === user.id : id === session.id) sessions.delete(id);
    }
    return [204, null];
  }

  throw new HttpError(404, "not_found", `No route for ${method} ${path}`);
}

function testRoute(method: string, path: string, body: Record<string, unknown>): Answer {
  if (method === "POST" && path === "/__test/users") {
    const user = newUser(body, { confirmed: true });
    return [201, { id: user.id, email: user.email }];
  }
  const [, email, sub] = /^\/__test\/users\/([^/]+)(\/confirmation)?$/.exec(path) ?? [];
  const user = email === undefined ? undefined : users.get(decodeURIComponent(email).toLowerCase());
  if (method === "GET" && sub !== undefined && user?.pending != null) {
    return [200, { url: `${AUTH_ISSUER}/verify?token=${user.pending.token}&type=signup&redirect_to=${encodeURIComponent(user.pending.redirectTo)}` }];
  }
  if (method === "PATCH" && sub === undefined && user !== undefined) {
    if (typeof body["amrAgeSeconds"] === "number") user.amrAgeSeconds = body["amrAgeSeconds"];
    if (typeof body["userFailures"] === "number") user.userFailures = body["userFailures"];
    if (typeof body["role"] === "string" || body["role"] === null) user.role = body["role"];
    return [200, { id: user.id, email: user.email }];
  }
  throw new HttpError(404, "not_found", "Unknown test user");
}

async function readJson(request: IncomingMessage): Promise<Record<string, unknown>> {
  let text = "";
  for await (const chunk of request) {
    text += String(chunk);
    if (text.length > MAX_BODY) throw new HttpError(413, "request_too_large", "Request body too large");
  }
  if (text === "") return {};
  try {
    const value: unknown = JSON.parse(text);
    return typeof value === "object" && value !== null ? (value as Record<string, unknown>) : {};
  } catch {
    throw new HttpError(400, "bad_json", "Could not parse request body as JSON");
  }
}

function send(response: ServerResponse, status: number, body: unknown, headers: Record<string, string> = {}): void {
  response.writeHead(status, { ...headers, ...(body === null ? {} : { "Content-Type": "application/json" }) });
  response.end(body === null ? undefined : JSON.stringify(body));
}

const server = createServer((request, response) => {
  if (!HOSTS.has(request.headers.host ?? "")) {
    send(response, 421, { code: 421, error_code: "misdirected_request", msg: "Unknown host" });
    return;
  }
  const url = new URL(request.url ?? "/", AUTH_URL);
  if (url.pathname.startsWith("/auth/v1/")) {
    // supabase-js calls Auth from the web app, as it calls a real Supabase project. No CORS on /__test.
    response.setHeader("Access-Control-Allow-Origin", WEB_ORIGIN);
    response.setHeader("Vary", "Origin");
    response.setHeader("Access-Control-Allow-Headers", "apikey, authorization, content-type, x-client-info, x-supabase-api-version");
    response.setHeader("Access-Control-Allow-Methods", "GET, POST, OPTIONS");
    response.setHeader("Access-Control-Expose-Headers", "x-supabase-api-version, retry-after");
    if (request.method === "OPTIONS") {
      send(response, 204, null);
      return;
    }
  }
  route(request, url).then(
    ([status, body, headers]) => send(response, status, body, headers),
    (error: unknown) => {
      if (error instanceof HttpError) {
        send(response, error.status, { code: error.status, error_code: error.errorCode, msg: error.message }, error.headers);
        return;
      }
      console.error(error);
      send(response, 500, { code: 500, error_code: "unexpected_failure", msg: "Unexpected failure" });
    },
  );
});

server.listen(AUTH_PORT, "localhost", () => {
  console.log(`Fake Supabase Auth on ${AUTH_URL}`);
});
