import "@angular/compiler";
import { Injector } from "@angular/core";
import { describe, expect, it } from "vitest";
import { ApiError, type ApiClient } from "../api/api-client";
import { API } from "../api/api";
import { type Me, PlayerStore } from "./player-store";

const ME = { id: "p", handle: "guest_a", walletBalance: 0 } as Me;

function store(getMe: () => Promise<Me>): PlayerStore {
  const api = { getMe } as unknown as ApiClient;
  return Injector.create({ providers: [{ provide: API, useValue: api }, { provide: PlayerStore }] }).get(PlayerStore);
}

describe("PlayerStore", () => {
  it("returns null without a guest session", async () => {
    const s = store(() => Promise.reject(new ApiError(401, "unauthenticated")));
    await expect(s.ensure()).resolves.toBeNull();
    expect(s.me()).toBeNull();
  });

  it("tries again after a failed refresh instead of keeping the failure", async () => {
    let calls = 0;
    const s = store(() => (++calls === 1 ? Promise.reject(new ApiError(0, "network_error")) : Promise.resolve(ME)));
    await expect(s.refresh()).rejects.toThrow();
    await expect(s.ensure()).resolves.toEqual(ME);
    expect(calls).toBe(2);
  });

  it("keeps the newest player when an older answer arrives last", async () => {
    const answers: ((me: Me) => void)[] = [];
    const s = store(() => new Promise<Me>((resolve) => answers.push(resolve)));
    const older = s.refresh();
    const newer = s.refresh();
    answers[1]?.({ ...ME, walletBalance: 100 });
    await newer;
    answers[0]?.({ ...ME, walletBalance: 400 });
    await older;
    expect(s.me()?.walletBalance).toBe(100);
  });
});
