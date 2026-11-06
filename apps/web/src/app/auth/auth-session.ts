import { Injectable, InjectionToken, inject, signal } from "@angular/core";
import type { Schemas } from "@vybe/contracts";
import type { AuthError, Session, SupabaseClient } from "@supabase/supabase-js";
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
      // Spec §11: no tokens in localStorage. The session lives in memory, so a reload asks to sign in again.
      persistSession: false,
      autoRefreshToken: true,
      // The email confirmation link returns with the session in the URL fragment (implicit flow).
      detectSessionInUrl: true,
      flowType: "implicit",
    },
  });
}

/** True when the URL holds a Supabase sign-in result (the email confirmation link). */
function hasAuthResult(): boolean {
  return typeof location !== "undefined" && /(^|[#&])(access_token|error_description)=/.test(location.hash.replace(/^#/, ""));
}

/**
 * The Supabase Auth session of this tab (spec §2 J3, ADR 0005). Signed-in players call the API with
 * the access token; everyone else uses the guest cookie. The session lives in memory only, so without a
 * sign-in in this tab (or a sign-in result in the URL) there is no session and no client to load.
 */
@Injectable({ providedIn: "root" })
export class AuthSession {
  private readonly supabase = inject(SUPABASE);
  private readonly statusSignal = signal<AuthStatus>("loading");
  private readonly userSignal = signal<AuthUser | null>(null);
  private config: Promise<AuthConfig | null> | null = null;
  private client: Promise<SupabaseClient> | null = null;

  readonly status = this.statusSignal.asReadonly();
  readonly user = this.userSignal.asReadonly();

  /** Loads the runtime config once (sets `status`); picks up a sign-in result from the URL. */
  async ready(): Promise<void> {
    if ((await this.loadConfig()) !== null && hasAuthResult()) await this.use();
  }

  /** A valid access token (refreshed when needed), or null when signed out. */
  async accessToken(): Promise<string | null> {
    await this.ready();
    if (this.client === null) return null;
    const { data, error } = await (await this.client).auth.getSession();
    if (error !== null) throw error;
    return data.session?.access_token ?? null;
  }

  async signIn(email: string, password: string): Promise<AuthResult> {
    const client = await this.use();
    if (client === null) return { ok: false, message: UNAVAILABLE };
    const { error } = await client.auth.signInWithPassword({ email: email.trim(), password });
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

  /** Ends the session in Supabase too (all devices), so its refresh token stops working. */
  async signOut(): Promise<void> {
    if (this.client === null) return;
    const client = await this.client;
    const { error } = await client.auth.signOut();
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
        if (config === null) this.statusSignal.set("off");
        else if (this.statusSignal() === "loading") this.statusSignal.set("signed-out");
        return config;
      },
      (error: unknown) => {
        console.error("Sign-in is unavailable", error);
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

function rememberAccount(): void {
  try {
    localStorage.setItem(ACCOUNT_FLAG, "1");
  } catch {
    // Storage blocked: the hint is optional.
  }
}
