import "@angular/compiler";
import { Injector } from "@angular/core";
import type { Schemas } from "@vybe/contracts";
import { describe, expect, it } from "vitest";
import { ApiClient } from "../api/api-client";
import { API } from "../api/api";
import { FlagsApi } from "./flags-api";
import { FLAG_LABELS } from "./flags-panel";

interface Call {
  url: string;
  method: string | undefined;
  headers: Record<string, string>;
}

function flagsApi(page: Schemas["FlaggedRunPage"]): { api: FlagsApi; calls: Call[] } {
  const calls: Call[] = [];
  const fetchFn = ((input: RequestInfo | URL, init?: RequestInit) => {
    calls.push({ url: String(input), method: init?.method, headers: (init?.headers ?? {}) as Record<string, string> });
    return Promise.resolve(new Response(JSON.stringify(page), { status: 200, headers: { "Content-Type": "application/json" } }));
  }) as typeof fetch;
  const client = new ApiClient(fetchFn);
  client.accessToken = () => Promise.resolve("staff-token");
  const api = Injector.create({ providers: [{ provide: API, useValue: client }, { provide: FlagsApi }] }).get(FlagsApi);
  return { api, calls };
}

describe("FlagsApi", () => {
  it("reads one page with the bearer token, the flag filter and the cursor", async () => {
    const { api, calls } = flagsApi({ items: [], nextCursor: null });
    await api.list("gap_variance_low", "abc");
    expect(calls[0]?.method).toBe("GET");
    expect(calls[0]?.url).toBe("/api/v1/admin/flagged-runs?flag=gap_variance_low&cursor=abc");
    expect(calls[0]?.headers["Authorization"]).toBe("Bearer staff-token");
  });

  it("sends no empty query parameters", async () => {
    const { api, calls } = flagsApi({ items: [], nextCursor: null });
    await api.list(undefined);
    expect(calls[0]?.url).toBe("/api/v1/admin/flagged-runs");
  });
});

describe("FLAG_LABELS", () => {
  it("has a label for every flag of the contract", () => {
    const all: Record<Schemas["ReviewFlag"], true> = {
      perfect_rate_high: true,
      gap_variance_low: true,
      aim_variance_low: true,
      daily_cap_streak: true,
    };
    expect(Object.keys(FLAG_LABELS).sort()).toEqual(Object.keys(all).sort());
  });
});
