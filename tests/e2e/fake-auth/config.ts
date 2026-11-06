/** Settings shared by the fake Supabase Auth server, the Playwright config and the specs. */
export const AUTH_PORT = Number(process.env["E2E_AUTH_PORT"] ?? 5290);
export const AUTH_URL = `http://localhost:${AUTH_PORT}`;
/** Token issuer, as Supabase names it: the project URL + `/auth/v1`. */
export const AUTH_ISSUER = `${AUTH_URL}/auth/v1`;
/** Test-only publishable key. The fake server checks that clients send it. */
export const AUTH_ANON_KEY = "vybe-e2e-anon-key";
