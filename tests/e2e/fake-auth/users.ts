import { AUTH_ANON_KEY, AUTH_TEST_SECRET, AUTH_URL } from "./config.ts";

const TEST_HEADERS = { "X-Test-Secret": AUTH_TEST_SECRET, "Content-Type": "application/json" };

export type Role = "moderator" | "administrator";

export interface TestUser {
  id: string;
  email: string;
  password: string;
}

export interface UserOptions {
  role?: Role;
  /** Seconds between the sign-in time in `amr` and now; > 300 makes deletion need a new sign-in. */
  amrAgeSeconds?: number;
}

/** A new confirmed Supabase user in the fake Auth; each call is a new identity. */
export async function createUser(options: UserOptions = {}): Promise<TestUser> {
  const email = `e2e-${crypto.randomUUID()}@vybe.test`;
  const password = `pw-${crypto.randomUUID()}`;
  const response = await fetch(`${AUTH_URL}/__test/users`, { method: "POST", headers: TEST_HEADERS, body: JSON.stringify({ email, password, ...options }) });
  if (response.status !== 201) throw new Error(`fake Auth answered ${response.status}`);
  const { id } = (await response.json()) as { id: string };
  return { id, email, password };
}

/** Changes a user's state in the fake Auth (sign-in time, outage of `GET /auth/v1/user`). */
export async function updateUser(user: TestUser, changes: { amrAgeSeconds?: number; userFailures?: number }): Promise<void> {
  const response = await fetch(`${AUTH_URL}/__test/users/${encodeURIComponent(user.email)}`, { method: "PATCH", headers: TEST_HEADERS, body: JSON.stringify(changes) });
  if (!response.ok) throw new Error(`fake Auth answered ${response.status}`);
}

/** A Supabase access token for the user (password grant), for API calls from the test. */
export async function accessToken(user: TestUser): Promise<string> {
  const response = await fetch(`${AUTH_URL}/auth/v1/token?grant_type=password`, {
    method: "POST",
    headers: { apikey: AUTH_ANON_KEY, "Content-Type": "application/json" },
    body: JSON.stringify({ email: user.email, password: user.password }),
  });
  if (!response.ok) throw new Error(`fake Auth answered ${response.status}`);
  return ((await response.json()) as { access_token: string }).access_token;
}

/** The link of the confirmation email of a pending sign-up (address with "+unconfirmed"). */
export async function confirmationUrl(email: string): Promise<string> {
  const response = await fetch(`${AUTH_URL}/__test/users/${encodeURIComponent(email)}/confirmation`, { headers: TEST_HEADERS });
  if (!response.ok) throw new Error(`fake Auth answered ${response.status}`);
  return ((await response.json()) as { url: string }).url;
}
