import "@angular/compiler";
import { Injector } from "@angular/core";
import { describe, expect, it } from "vitest";
import { AccountApi } from "./account-api";
import { ApiClient, ApiError, isApiError } from "./api-client";
import { API } from "./api";

interface Call {
  url: string;
  headers: Record<string, string>;
  body: unknown;
}

function client(token: string | null | Error, respond: () => Response = () => new Response("{}", { status: 200 })): { api: ApiClient; calls: Call[] } {
  const calls: Call[] = [];
  const fetchFn = ((input: RequestInfo | URL, init?: RequestInit) => {
    calls.push({ url: String(input), headers: (init?.headers ?? {}) as Record<string, string>, body: init?.body });
    return Promise.resolve(respond());
  }) as typeof fetch;
  const api = new ApiClient(fetchFn);
  api.accessToken = () => (token instanceof Error ? Promise.reject(token) : Promise.resolve(token));
  return { api, calls };
}

function accounts(api: ApiClient): AccountApi {
  return Injector.create({ providers: [{ provide: API, useValue: api }, { provide: AccountApi }] }).get(AccountApi);
}

describe("bearer token hook", () => {
  it("sends the Supabase access token of a signed-in player", async () => {
    const { api, calls } = client("token-1");
    await api.getMe();
    expect(calls[0]?.headers["Authorization"]).toBe("Bearer token-1");
  });

  it("sends no Authorization header on the guest cookie path", async () => {
    const { api, calls } = client(null);
    await api.getMe();
    expect(calls[0]?.headers["Authorization"]).toBeUndefined();
  });

  it("never sends the bearer token to createGuest or linkAccount (cookie only, CSRF header)", async () => {
    const { api, calls } = client("token-1");
    await api.createGuest({ birthYear: 1990, birthMonth: 6 });
    await accounts(api).linkAccount("proof");
    for (const call of calls) expect(call.headers["Authorization"]).toBeUndefined();
    expect(calls[1]?.url).toBe("/api/v1/accounts/link");
    expect(calls[1]?.headers["X-Requested-With"]).toBe("vybe-web");
    expect(calls[1]?.body).toBe('{"linkProof":"proof"}');
  });

  it("fails as network_error when the token cannot be refreshed, instead of falling back to the cookie", async () => {
    const { api, calls } = client(new Error("refresh failed"));
    const error = await api.getMe().catch((e: unknown) => e);
    expect(isApiError(error, "network_error")).toBe(true);
    expect(calls).toHaveLength(0);
  });

  it("reads Retry-After seconds from 429 and 503 answers", async () => {
    const problem = { type: "t", title: "Unavailable", status: 503, code: "auth_unavailable", traceId: "x" };
    const { api } = client(null, () => new Response(JSON.stringify(problem), { status: 503, headers: { "Retry-After": "30" } }));
    const error = (await accounts(api).linkAccount("proof").catch((e: unknown) => e)) as ApiError;
    expect(error.code).toBe("auth_unavailable");
    expect(error.retryAfterSeconds).toBe(30);
  });

  it("encodes ids in admin and privacy paths", async () => {
    const { api, calls } = client("t", () => new Response(null, { status: 204 }));
    await accounts(api).unblock("a/b");
    expect(calls[0]?.url).toBe("/api/v1/me/blocks/a%2Fb");
  });
});
