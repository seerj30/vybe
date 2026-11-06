import "@angular/compiler";
import { Injector } from "@angular/core";
import type { SupabaseClient } from "@supabase/supabase-js";
import { describe, expect, it } from "vitest";
import { AuthSession, SUPABASE, type SupabaseLoader, authErrorMessage } from "./auth-session";
import type { AuthConfig } from "./runtime-config";

const CONFIG: AuthConfig = { supabaseUrl: "https://abc.supabase.co", supabaseAnonKey: "k" };

interface FakeSession {
  access_token: string;
  user: { id: string; email: string; app_metadata: Record<string, unknown> };
}

function fakeClient(): SupabaseClient {
  let session: FakeSession | null = null;
  let listener: ((event: string, s: FakeSession | null) => void) | null = null;
  const auth = {
    onAuthStateChange: (fn: typeof listener) => (listener = fn),
    getSession: () => Promise.resolve({ data: { session }, error: null }),
    signInWithPassword: ({ email }: { email: string }) => {
      session = { access_token: "token-1", user: { id: "u1", email, app_metadata: { vybe_role: "moderator" } } };
      listener?.("SIGNED_IN", session);
      return Promise.resolve({ error: null });
    },
    signOut: () => {
      session = null;
      listener?.("SIGNED_OUT", null);
      return Promise.resolve({ error: null });
    },
  };
  return { auth } as unknown as SupabaseClient;
}

function session(config: () => Promise<AuthConfig | null>): { auth: AuthSession; created: () => number } {
  let created = 0;
  const loader: SupabaseLoader = {
    config,
    create: () => {
      created++;
      return Promise.resolve(fakeClient());
    },
  };
  const auth = Injector.create({ providers: [{ provide: SUPABASE, useValue: loader }, { provide: AuthSession }] }).get(AuthSession);
  return { auth, created: () => created };
}

describe("AuthSession", () => {
  it("is off without a runtime config, and has no token", async () => {
    const { auth } = session(() => Promise.resolve(null));
    await expect(auth.accessToken()).resolves.toBeNull();
    expect(auth.status()).toBe("off");
  });

  it("is unavailable when the config cannot be read", async () => {
    const { auth } = session(() => Promise.reject(new Error("500")));
    await auth.ready();
    expect(auth.status()).toBe("unavailable");
  });

  it("loads supabase-js only for a sign-in, then sends its token and role", async () => {
    const { auth, created } = session(() => Promise.resolve(CONFIG));
    await expect(auth.accessToken()).resolves.toBeNull();
    expect(auth.status()).toBe("signed-out");
    expect(created()).toBe(0);

    await expect(auth.signIn(" ace@vybe.test ", "pw")).resolves.toEqual({ ok: true });
    expect(created()).toBe(1);
    expect(auth.status()).toBe("signed-in");
    expect(auth.user()).toEqual({ id: "u1", email: "ace@vybe.test", role: "moderator" });
    await expect(auth.accessToken()).resolves.toBe("token-1");

    await auth.signOut();
    expect(auth.status()).toBe("signed-out");
    await expect(auth.accessToken()).resolves.toBeNull();
  });

  it("maps Auth error codes to player-facing text", () => {
    expect(authErrorMessage({ code: "invalid_credentials", status: 400 })).toBe("The email or the password is not correct.");
    expect(authErrorMessage({ code: undefined, status: 0 })).toContain("No connection");
  });
});
