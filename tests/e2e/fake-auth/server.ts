/**
 * Fake Supabase Auth for the browser tests only (never for a real deployment). It speaks the subset of
 * the GoTrue API that supabase-js and the VYBE API use: password sign-in, refresh, sign-up, sign-out,
 * `GET /auth/v1/user`, the issuer metadata and the JWKS. Tokens are ES256 JWTs with the claims of a
 * real Supabase access token (`amr`, `app_metadata`, `is_anonymous`).
 *
 * Test-only endpoints under `/__test` create users and change their state:
 * - `POST /__test/users` `{ email, password, role?, confirmed?, amrAgeSeconds? }`
 * - `PATCH /__test/users/{email}` `{ role?, amrAgeSeconds?, userFailures? }`
 * `amrAgeSeconds` moves the sign-in time (`amr[].timestamp`) into the past; `userFailures` makes the
 * next N calls of `GET /auth/v1/user` answer 503 (Auth outage).
 *
 * Run: `node tests/e2e/fake-auth/server.ts` (port `E2E_AUTH_PORT`, default 5290).
 */
import { createHash, generateKeyPairSync, randomBytes, randomUUID, sign, verify } from "node:crypto";
import { createServer, type IncomingMessage, type ServerResponse } from "node:http";
import { AUTH_ANON_KEY, AUTH_ISSUER, AUTH_PORT, AUTH_URL } from "./config.ts";

interface User {
  id: string;
  email: string;
  password: string;
  role: string | null;
  confirmed: boolean;
  amrAgeSeconds: number;
  userFailures: number;
  createdAt: string;
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
  };
  users.set(email, user);
  return user;
}

async function route(request: IncomingMessage, url: URL): Promise<[number, unknown]> {
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

  if (path.startsWith("/__test/")) return testRoute(method, path, await readJson(request));

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
    throw new HttpError(400, "unsupported_grant_type", "unsupported_grant_type");
  }

  if (method === "POST" && path === "/auth/v1/signup") {
    // Addresses with "+unconfirmed" behave like "Confirm email" on: no session until the email is confirmed.
    const body = await readJson(request);
    const user = newUser({ email: body["email"], password: body["password"] }, { confirmed: !String(body["email"]).includes("+unconfirmed") });
    return [200, user.confirmed ? signIn(user) : userJson(user)];
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

function testRoute(method: string, path: string, body: Record<string, unknown>): [number, unknown] {
  if (method === "POST" && path === "/__test/users") {
    const user = newUser(body, { confirmed: true });
    return [201, { id: user.id, email: user.email }];
  }
  const email = /^\/__test\/users\/([^/]+)$/.exec(path)?.[1];
  const user = email === undefined ? undefined : users.get(decodeURIComponent(email).toLowerCase());
  if (method === "PATCH" && user !== undefined) {
    if (typeof body["amrAgeSeconds"] === "number") user.amrAgeSeconds = body["amrAgeSeconds"];
    if (typeof body["userFailures"] === "number") user.userFailures = body["userFailures"];
    if (typeof body["role"] === "string" || body["role"] === null) user.role = body["role"];
    return [200, { id: user.id, email: user.email }];
  }
  throw new HttpError(404, "not_found", "Unknown test user");
}

async function readJson(request: IncomingMessage): Promise<Record<string, unknown>> {
  let text = "";
  for await (const chunk of request) text += String(chunk);
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
  // supabase-js calls Auth from the web origin, as it calls a real Supabase project.
  response.setHeader("Access-Control-Allow-Origin", "*");
  response.setHeader("Access-Control-Allow-Headers", "apikey, authorization, content-type, x-client-info, x-supabase-api-version");
  response.setHeader("Access-Control-Allow-Methods", "GET, POST, PATCH, OPTIONS");
  response.setHeader("Access-Control-Expose-Headers", "x-supabase-api-version, retry-after");
  if (request.method === "OPTIONS") {
    send(response, 204, null);
    return;
  }
  const url = new URL(request.url ?? "/", AUTH_URL);
  route(request, url).then(
    ([status, body]) => send(response, status, body),
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
