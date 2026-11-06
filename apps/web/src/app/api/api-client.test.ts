import { describe, expect, it } from "vitest";
import { ApiClient, ApiError, isApiError, newIdempotencyKey } from "./api-client";

interface Call {
  url: string;
  init: RequestInit;
}

function fakeFetch(respond: (call: Call) => Response | Promise<Response>): { fetch: typeof fetch; calls: Call[] } {
  const calls: Call[] = [];
  const fn = (input: RequestInfo | URL, init?: RequestInit): Promise<Response> => {
    const call = { url: String(input), init: init ?? {} };
    calls.push(call);
    return Promise.resolve(respond(call));
  };
  return { fetch: fn as typeof fetch, calls };
}

function json(status: number, body: unknown, type = "application/json"): Response {
  return new Response(JSON.stringify(body), { status, headers: { "Content-Type": type } });
}

function headers(call: Call | undefined): Record<string, string> {
  return (call?.init.headers ?? {}) as Record<string, string>;
}

describe("ApiClient", () => {
  it("sends the CSRF header and same-origin credentials on unsafe requests", async () => {
    const { fetch, calls } = fakeFetch(() => json(201, { playerId: "p", handle: "guest_x", guestExpiresAt: "t" }));
    const guest = await new ApiClient(fetch).createGuest();

    expect(guest.handle).toBe("guest_x");
    expect(calls[0]?.url).toBe("/api/v1/guests");
    expect(calls[0]?.init.method).toBe("POST");
    expect(calls[0]?.init.credentials).toBe("same-origin");
    expect(headers(calls[0])["X-Requested-With"]).toBe("vybe-web");
  });

  it("does not send the CSRF header on GET", async () => {
    const { fetch, calls } = fakeFetch(() => json(200, {}));
    await new ApiClient(fetch).getMe();
    expect(headers(calls[0])["X-Requested-With"]).toBeUndefined();
  });

  it("sends a JSON body, the Idempotency-Key and the query string", async () => {
    const { fetch, calls } = fakeFetch(() => json(200, { ok: true }));
    await new ApiClient(fetch).request("POST", "/things", { body: { a: 1 }, idempotencyKey: "k-1", query: { limit: 5, cursor: undefined } });

    expect(calls[0]?.url).toBe("/api/v1/things?limit=5");
    expect(calls[0]?.init.body).toBe('{"a":1}');
    expect(headers(calls[0])["Content-Type"]).toBe("application/json");
    expect(headers(calls[0])["Idempotency-Key"]).toBe("k-1");
  });

  it("turns a problem response into an ApiError with the contract code", async () => {
    const problem = { type: "/problems/revision_conflict", title: "Conflict", status: 409, code: "revision_conflict", traceId: "x" };
    const { fetch } = fakeFetch(() => json(409, problem, "application/problem+json"));

    const error = await new ApiClient(fetch).getAvatar().catch((e: unknown) => e);
    expect(isApiError(error, "revision_conflict")).toBe(true);
    expect((error as ApiError).status).toBe(409);
    expect((error as ApiError).problem).toEqual(problem);
    expect((error as ApiError).retryable).toBe(false);
  });

  it("reports a network failure as a retryable network_error", async () => {
    const failing = (() => Promise.reject(new TypeError("Failed to fetch"))) as typeof fetch;
    const error = await new ApiClient(failing).getMe().catch((e: unknown) => e);
    expect(isApiError(error, "network_error")).toBe(true);
    expect((error as ApiError).retryable).toBe(true);
  });

  it("reports a non-JSON gateway error as a retryable internal_error", async () => {
    const { fetch } = fakeFetch(() => new Response("<html>Bad gateway</html>", { status: 502 }));
    const error = await new ApiClient(fetch).getMe().catch((e: unknown) => e);
    expect(isApiError(error, "internal_error")).toBe(true);
    expect((error as ApiError).retryable).toBe(true);
  });

  it("treats request_in_progress as retryable", () => {
    expect(new ApiError(409, "request_in_progress").retryable).toBe(true);
    expect(new ApiError(409, "insufficient_funds").retryable).toBe(false);
  });
});

describe("newIdempotencyKey", () => {
  it("returns a fresh UUID each time", () => {
    const a = newIdempotencyKey();
    expect(a).toMatch(/^[0-9a-f]{8}-[0-9a-f]{4}-4[0-9a-f]{3}-[89ab][0-9a-f]{3}-[0-9a-f]{12}$/);
    expect(newIdempotencyKey()).not.toBe(a);
  });
});
