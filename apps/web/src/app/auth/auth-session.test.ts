import "@angular/compiler";
import { Injector } from "@angular/core";
import type { SupabaseClient } from "@supabase/supabase-js";
import { describe, expect, it } from "vitest";
import { AuthSession, SUPABASE, type SupabaseLoader, authErrorMessage, tabStorage } from "./auth-session";
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

  it("fails instead of falling back to the guest cookie when the session ends without a sign-out", async () => {
    const { auth } = session(() => Promise.resolve(CONFIG));
    await auth.signIn("ace@vybe.test", "pw");
    const client = (await (auth as unknown as { client: Promise<SupabaseClient> }).client).auth as unknown as { signOut: () => Promise<unknown> };
    await client.signOut(); // as supabase-js does when the refresh token is revoked
    await expect(auth.accessToken()).rejects.toMatchObject({ code: "unauthenticated" });
  });

  it("keeps PKCE verifiers in tab storage and tokens in memory", () => {
    const tab = new Map<string, string>();
    const storage = tabStorage({
      getItem: (k) => tab.get(k) ?? null,
      setItem: (k, v) => void tab.set(k, v),
      removeItem: (k) => void tab.delete(k),
    });
    storage.setItem("vybe-auth-1-flow-abc-code-verifier", "v");
    storage.setItem("vybe-auth-1", '{"access_token":"t"}');
    expect([...tab.keys()]).toEqual(["vybe-auth-1-flow-abc-code-verifier"]);
    expect(storage.getItem("vybe-auth-1")).toBe('{"access_token":"t"}');
    storage.removeItem("vybe-auth-1-flow-abc-code-verifier");
    expect(tab.size).toBe(0);
  });

  it("maps Auth error codes to player-facing text", () => {
    expect(authErrorMessage({ code: "invalid_credentials", status: 400 })).toBe("The email or the password is not correct.");
    expect(authErrorMessage({ code: undefined, status: 0 })).toContain("No connection");
  });
});
