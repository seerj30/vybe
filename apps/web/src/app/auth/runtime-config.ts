/**
 * Supabase settings come from `/runtime-config.json`, which the hosting serves next to the app, so one
 * build runs in every environment. The publishable (anon) key is public by design; no secret is in it.
 * Without the file (404, or HTML from a single-page-app fallback), sign-in is off and the app is guest-only.
 */
export interface AuthConfig {
  supabaseUrl: string;
  supabaseAnonKey: string;
}

export const RUNTIME_CONFIG_PATH = "/runtime-config.json";

/** The config, or null when the hosting serves none. Throws when the file cannot be read or is invalid. */
export async function loadAuthConfig(fetchFn: typeof fetch = (input, init) => fetch(input, init)): Promise<AuthConfig | null> {
  const response = await fetchFn(RUNTIME_CONFIG_PATH, { headers: { Accept: "application/json" }, cache: "no-store" });
  if (response.status === 404) return null;
  if (!response.ok) throw new Error(`${RUNTIME_CONFIG_PATH} answered ${response.status}`);
  // A single-page-app host answers unknown paths with index.html: no config file.
  if (!(response.headers.get("Content-Type") ?? "").includes("json")) return null;
  const config = parseAuthConfig(await response.json());
  if (config === null) throw new Error(`${RUNTIME_CONFIG_PATH} is not valid`);
  return config;
}

/** Accepts an https Supabase URL (http only for a local host) and a non-empty key. */
export function parseAuthConfig(value: unknown): AuthConfig | null {
  if (typeof value !== "object" || value === null) return null;
  const { supabaseUrl, supabaseAnonKey } = value as Record<string, unknown>;
  if (typeof supabaseUrl !== "string" || typeof supabaseAnonKey !== "string") return null;
  if (supabaseAnonKey.length === 0 || supabaseAnonKey.length > 4096) return null;
  let url: URL;
  try {
    url = new URL(supabaseUrl);
  } catch {
    return null;
  }
  const local = url.hostname === "localhost" || url.hostname === "127.0.0.1" || url.hostname === "[::1]";
  if (url.protocol !== "https:" && !(url.protocol === "http:" && local)) return null;
  return { supabaseUrl: url.origin, supabaseAnonKey };
}
