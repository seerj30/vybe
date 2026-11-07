import { Injectable, InjectionToken, Injector, inject, signal } from "@angular/core";
import type { Schemas } from "@vybe/contracts";
import type { AuthError, Session, SupabaseClient } from "@supabase/supabase-js";
import { ApiError } from "../api/api-client";
import { PlayerStore } from "../player/player-store";
import { applyContentSecurityPolicy } from "./content-security-policy";
import { type AuthConfig, loadAuthConfig } from "./runtime-config";

export type AuthStatus = "loading" | "off" | "unavailable" | "signed-out" | "signed-in";
export type AdminRole = Schemas["AdminRole"];

export interface AuthUser {
  id: string;
  email: string;
  /** From `app_metadata.vybe_role`, which only the Supabase service role can write. The API decides access. */
  role: AdminRole | null;
}

export type AuthResult = { ok: true } | { ok: false; message: string };
export type SignUpResult = { kind: "signed-in" } | { kind: "confirm-email" } | { kind: "error"; message: string };

const ACCOUNT_FLAG = "vybe.hadAccount";

/** Loads the runtime config and creates the Supabase client. Tests provide their own. */
export interface SupabaseLoader {
  /** Null when the hosting serves no runtime config (sign-in off); throws when it cannot be read. */
  config(): Promise<AuthConfig | null>;
  create(config: AuthConfig): Promise<SupabaseClient>;
}

export const SUPABASE = new InjectionToken<SupabaseLoader>("Supabase", {
  providedIn: "root",
  factory: () => ({ config: () => loadAuthConfig(), create: createSupabaseClient }),
});

/** supabase-js is a lazy chunk: it loads only when someone signs in. */
async function createSupabaseClient(config: AuthConfig): Promise<SupabaseClient> {
  const { createClient } = await import("@supabase/supabase-js");
  return createClient(config.supabaseUrl, config.supabaseAnonKey, {
    auth: {
      // PKCE: a sign-in result in the URL (`?code=`) counts only with the code verifier that this tab
      // stored when it started the sign-up, so nobody can sign this browser in with a crafted link
      // (login CSRF). `#access_token` links are refused.
      flowType: "pkce",
      detectSessionInUrl: true,
      autoRefreshToken: true,
      // Spec §11: no tokens in web storage. Tokens stay in memory; only the PKCE verifier goes to
      // sessionStorage (`tabStorage`). A reload asks to sign in again.
      persistSession: true,
      storage: tabStorage(),
      // One key per tab: supabase-js shares session events between tabs with the same key.
      storageKey: `vybe-auth-${tabId()}`,
    },
  });
}

/** Storage adapter: PKCE verifier keys in sessionStorage (they must survive the email-link reload), the rest in memory. */
export function tabStorage(session: Pick<Storage, "getItem" | "setItem" | "removeItem"> | null = sessionStorageOrNull()): TabStorage {
  const memory = new Map<string, string>();
  const store = (key: string): Pick<Storage, "getItem" | "setItem" | "removeItem"> | null =>
    key.endsWith("-code-verifier") ? session : null;
  return {
    getItem: (key) => store(key)?.getItem(key) ?? memory.get(key) ?? null,
    setItem: (key, value) => {
      const target = store(key);
      if (target === null) memory.set(key, value);
      else target.setItem(key, value);
    },
    removeItem: (key) => {
      store(key)?.removeItem(key);
      memory.delete(key);
    },
  };
}

export interface TabStorage {
  getItem(key: string): string | null;
  setItem(key: string, value: string): void;
  removeItem(key: string): void;
}

function sessionStorageOrNull(): Storage | null {
  try {
    return typeof sessionStorage === "undefined" ? null : sessionStorage;
  } catch {
    return null; // storage blocked: PKCE verifiers stay in memory
  }
}

/** A random id per tab, kept across reloads of the same tab. */
function tabId(): string {
  const storage = sessionStorageOrNull();
  let id = storage?.getItem("vybe.tabId") ?? null;
  if (id === null) {
    id = crypto.randomUUID();
    storage?.setItem("vybe.tabId", id);
  }
  return id;
}

/** True when the URL is a PKCE sign-in callback (`?code=`), for example from the email confirmation link. */
function hasAuthResult(): boolean {
  return typeof location !== "undefined" && new URLSearchParams(location.search).has("code");
}

/**
 * The Supabase Auth session of this tab (spec §2 J3, ADR 0005). Signed-in players call the API with
 * the access token; everyone else uses the guest cookie. The session lives in memory only, so without a
 * sign-in in this tab (or a sign-in callback in the URL) there is no session and no client to load.
 */
@Injectable({ providedIn: "root" })
export class AuthSession {
  private readonly supabase = inject(SUPABASE);
  // Lazy: PlayerStore → API → AuthSession would be a cycle at construction.
  private readonly injector = inject(Injector);
  private readonly statusSignal = signal<AuthStatus>("loading");
  private readonly userSignal = signal<AuthUser | null>(null);
  private readonly linkFailedSignal = signal(false);
  private callback: Promise<void> | null = null;
  private config: Promise<AuthConfig | null> | null = null;
  private client: Promise<SupabaseClient> | null = null;
  /** A session existed and the user did not sign out: a missing session now is an error, not "guest". */
  private expectSession = false;

  readonly status = this.statusSignal.asReadonly();
  readonly user = this.userSignal.asReadonly();
  /**
   * True when the URL had a sign-in code (email link) but this tab could not use it: the PKCE verifier
   * lives only in the tab that signed up. The email address is confirmed; the user signs in with the password.
   */
  readonly linkFailed = this.linkFailedSignal.asReadonly();

  /** Loads the runtime config once (sets `status`); picks up a sign-in callback from the URL. */
  async ready(): Promise<void> {
    if ((await this.loadConfig()) === null) return;
    if (this.callback === null && hasAuthResult()) this.callback = this.takeCallback();
    await this.callback;
  }

  /** Lets supabase-js exchange the URL code once, then removes the code from the address. */
  private async takeCallback(): Promise<void> {
    await this.use();
    if (this.statusSignal() !== "signed-in") this.linkFailedSignal.set(true);
    const url = new URL(location.href);
    url.searchParams.delete("code");
    url.searchParams.delete("sb_flow_id");
    history.replaceState(history.state, "", url.pathname + url.search + url.hash);
  }

  /**
   * A valid access token (refreshed when needed), or null when signed out. When the session ended
   * without a sign-out (refresh token revoked), it throws: sending the guest cookie instead could act
   * as another player.
   */
  async accessToken(): Promise<string | null> {
    await this.ready();
    if (this.client === null) return null;
    const { data, error } = await (await this.client).auth.getSession();
    if (error !== null) throw error;
    if (data.session === null && this.expectSession) throw new ApiError(401, "unauthenticated");
    return data.session?.access_token ?? null;
  }

  async signIn(email: string, password: string): Promise<AuthResult> {
    const client = await this.use();
    if (client === null) return { ok: false, message: UNAVAILABLE };
    const { error } = await client.auth.signInWithPassword({ email: email.trim(), password });
    if (error === null) this.linkFailedSignal.set(false);
    return error === null ? { ok: true } : { ok: false, message: authErrorMessage(error) };
  }

  /** New account. With "Confirm email" on, Supabase sends a link and returns no session yet. */
  async signUp(email: string, password: string): Promise<SignUpResult> {
    const client = await this.use();
    if (client === null) return { kind: "error", message: UNAVAILABLE };
    const { data, error } = await client.auth.signUp({
      email: email.trim(),
      password,
      options: { emailRedirectTo: `${location.origin}/` },
    });
    if (error !== null) return { kind: "error", message: authErrorMessage(error) };
    return data.session === null ? { kind: "confirm-email" } : { kind: "signed-in" };
  }

  /** A fresh sign-in of the current user, for the deletion check (newest `amr` timestamp, ADR 0005 #8). */
  async reauthenticate(password: string): Promise<AuthResult> {
    const email = this.userSignal()?.email;
    if (email === undefined) return { ok: false, message: "Sign in first." };
    return this.signIn(email, password);
  }

  /**
   * Ends the session of this browser (`local`, the default: other devices stay signed in) or of every
   * device (`global`, after an account deletion). It also forgets the cached player.
   */
  async signOut(scope: "local" | "global" = "local"): Promise<void> {
    this.expectSession = false;
    forgetAccount();
    this.injector.get(PlayerStore).clear();
    if (this.client === null) return;
    const client = await this.client;
    const { error } = await client.auth.signOut({ scope });
    if (error !== null) {
      // Supabase did not answer: end the session in this tab at least.
      await client.auth.signOut({ scope: "local" });
    }
  }

  /** True when this browser was signed in before, so a missing session means "sign in", not "lost guest". */
  hadAccount(): boolean {
    try {
      return localStorage.getItem(ACCOUNT_FLAG) === "1";
    } catch {
      return false;
    }
  }

  private loadConfig(): Promise<AuthConfig | null> {
    this.config ??= this.supabase.config().then(
      (config) => {
        applyContentSecurityPolicy(config?.supabaseUrl ?? null);
        if (config === null) this.statusSignal.set("off");
        else if (this.statusSignal() === "loading") this.statusSignal.set("signed-out");
        return config;
      },
      (error: unknown) => {
        console.error("Sign-in is unavailable", error);
        applyContentSecurityPolicy(null);
        this.statusSignal.set("unavailable");
        return null;
      },
    );
    return this.config;
  }

  /** The client, created on first use; null when sign-in is off or supabase-js does not load. */
  private async use(): Promise<SupabaseClient | null> {
    const config = await this.loadConfig();
    if (config === null) return null;
    this.client ??= this.supabase.create(config).then(async (client) => {
      client.auth.onAuthStateChange((_event, session) => this.apply(session));
      const { data } = await client.auth.getSession();
      this.apply(data.session);
      return client;
    });
    try {
      return await this.client;
    } catch (error: unknown) {
      console.error("Sign-in is unavailable", error);
      this.client = null; // the next attempt loads again
      return null;
    }
  }

  private apply(session: Session | null): void {
    if (session === null) {
      this.userSignal.set(null);
      this.statusSignal.set("signed-out");
      return;
    }
    const role = session.user.app_metadata["vybe_role"];
    this.userSignal.set({
      id: session.user.id,
      email: session.user.email ?? "",
      role: role === "moderator" || role === "administrator" ? role : null,
    });
    this.statusSignal.set("signed-in");
    this.expectSession = true;
    rememberAccount();
  }
}

const UNAVAILABLE = "Sign-in is not available right now. Try again later.";

/** Player-facing text for Supabase Auth errors (codes of the Auth API). */
export function authErrorMessage(error: Pick<AuthError, "code" | "status">): string {
  switch (error.code) {
    case "invalid_credentials":
      return "The email or the password is not correct.";
    case "email_not_confirmed":
      return "Confirm your email address first. Open the link in the email that you got.";
    case "user_already_exists":
    case "email_exists":
      return "An account with this email address exists. Sign in instead.";
    case "weak_password":
      return "The password is too weak. Use a longer password.";
    case "validation_failed":
    case "email_address_invalid":
      return "Enter a valid email address.";
    case "over_request_rate_limit":
    case "over_email_send_rate_limit":
      return "Too many attempts. Wait a minute and try again.";
    default:
      return error.status === 0 || error.status === undefined
        ? "No connection to the sign-in service. Check your connection and try again."
        : "Sign-in did not work. Try again.";
  }
}

function forgetAccount(): void {
  try {
    localStorage.removeItem(ACCOUNT_FLAG);
  } catch {
    // Storage blocked: nothing to forget.
  }
}

function rememberAccount(): void {
  try {
    localStorage.setItem(ACCOUNT_FLAG, "1");
  } catch {
    // Storage blocked: the hint is optional.
  }
}
