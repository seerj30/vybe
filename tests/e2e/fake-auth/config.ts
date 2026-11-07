/** Settings shared by the fake Supabase Auth server, the Playwright config and the specs. */
export const AUTH_PORT = Number(process.env["E2E_AUTH_PORT"] ?? 5290);
export const AUTH_URL = `http://localhost:${AUTH_PORT}`;
/** Token issuer, as Supabase names it: the project URL + `/auth/v1`. */
export const AUTH_ISSUER = `${AUTH_URL}/auth/v1`;
/** Test-only publishable key. The fake server checks that clients send it. */
export const AUTH_ANON_KEY = "vybe-e2e-anon-key";
/** The web app origin, the only origin that may call Auth from a page (CORS). */
export const WEB_ORIGIN = `http://localhost:${process.env["E2E_WEB_PORT"] ?? 4321}`;

/**
 * Value of the `X-Test-Secret` header that the `/__test` endpoints require. There is no default:
 * `playwright.config.ts` makes a random one per run and gives it to the server and the specs.
 */
export function testSecret(): string {
  const secret = process.env["E2E_AUTH_TEST_SECRET"];
  if (secret === undefined || secret.length < 16) throw new Error("E2E_AUTH_TEST_SECRET (16+ characters) is not set");
  return secret;
}
