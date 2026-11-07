import "@angular/compiler";
import { Injector } from "@angular/core";
import { describe, expect, it } from "vitest";
import { AccountApi } from "../api/account-api";
import { ApiError, type ApiClient } from "../api/api-client";
import { API } from "../api/api";
import { AgeGate } from "../player/age-gate";
import { type Me, PlayerStore } from "../player/player-store";
import { AccountLink } from "./account-link";
import { AuthSession } from "./auth-session";

const GUEST = { id: "p-guest", handle: "guest_a", kind: "guest" } as Me;
const ACCOUNT = { id: "p-account", handle: "ace", kind: "registered" } as Me;

interface World {
  account: Me | ApiError;
  guest: Me | ApiError;
  link?: ApiError;
  /** The age-gate answer; null = cancelled. */
  birthYear?: number | null;
}

function setup(world: World): { link: AccountLink; log: string[] } {
  const log: string[] = [];
  let linked = false;
  const answer = (value: Me | ApiError): Promise<Me> => (value instanceof ApiError ? Promise.reject(value) : Promise.resolve(value));
  const api = {
    getMe: () => answer(linked ? { ...GUEST, kind: "registered" } : world.account),
    createGuest: (birthYear: number) => {
      log.push(`createGuest:${birthYear}`);
      world.guest = GUEST;
      return Promise.resolve({});
    },
  } as unknown as ApiClient;
  const accounts = {
    getGuest: () => answer(world.guest),
    linkAccount: (proof: string) => {
      log.push(`link:${proof}`);
      if (world.link !== undefined) return Promise.reject(world.link);
      linked = true;
      return Promise.resolve({ playerId: GUEST.id, accountLinked: true });
    },
  } as unknown as AccountApi;
  const auth = { accessToken: () => Promise.resolve("token-1") } as unknown as AuthSession;
  const player = { refresh: () => api.getMe() } as unknown as PlayerStore;
  const injector = Injector.create({
    providers: [
      { provide: API, useValue: api },
      { provide: AccountApi, useValue: accounts },
      { provide: AuthSession, useValue: auth },
      { provide: PlayerStore, useValue: player },
      { provide: AgeGate, useValue: { ask: () => Promise.resolve(world.birthYear === undefined ? 1990 : world.birthYear) } },
      { provide: AccountLink },
    ],
  });
  return { link: injector.get(AccountLink), log };
}

const noPlayer = new ApiError(404, "player_not_found");
const noGuest = new ApiError(401, "unauthenticated");

describe("AccountLink", () => {
  it("resolve only reads: an identity without a player asks to confirm the link", async () => {
    const { link, log } = setup({ account: noPlayer, guest: GUEST });
    expect(await link.resolve()).toEqual({ kind: "confirm", guest: GUEST });
    expect(log).toEqual([]);
  });

  it("links the guest of this browser after the confirmation: same player id", async () => {
    const { link, log } = setup({ account: noPlayer, guest: GUEST });
    const outcome = await link.link();
    expect(outcome).toEqual({ kind: "ready", me: { ...GUEST, kind: "registered" }, isNew: false });
    expect(log).toEqual(["link:token-1"]);
  });

  it("creates a guest first when the browser has none", async () => {
    const { link, log } = setup({ account: noPlayer, guest: noGuest });
    expect(await link.resolve()).toEqual({ kind: "confirm", guest: null });
    const outcome = await link.link();
    expect(outcome.kind === "ready" && outcome.isNew).toBe(true);
    expect(log).toEqual(["createGuest:1990", "link:token-1"]);
  });

  it("goes back to the confirmation when the age gate is cancelled, and creates nothing", async () => {
    const { link, log } = setup({ account: noPlayer, guest: noGuest, birthYear: null });
    expect(await link.link()).toEqual({ kind: "confirm", guest: null });
    expect(log).toEqual([]);
  });

  it("rejects an under-age answer without linking", async () => {
    const { link, log } = setup({ account: noPlayer, guest: noGuest });
    const under = new ApiError(403, "age_not_eligible");
    (link as unknown as { api: { createGuest: () => Promise<never> } }).api.createGuest = () => Promise.reject(under);
    const outcome = await link.link();
    expect(outcome.kind).toBe("rejected");
    expect(log).toEqual([]);
  });

  it("signs in to an identity that has a player, without linking", async () => {
    const { link, log } = setup({ account: ACCOUNT, guest: noGuest });
    expect(await link.resolve()).toEqual({ kind: "ready", me: ACCOUNT, isNew: false });
    expect(log).toEqual([]);
  });

  it("asks the user to choose when the identity has a player and the browser has another guest", async () => {
    const { link, log } = setup({ account: ACCOUNT, guest: GUEST });
    expect(await link.resolve()).toEqual({ kind: "choose", account: ACCOUNT, guest: GUEST });
    expect(log).toEqual([]);
  });

  it("explains a conflict and merges nothing (409 identity_already_linked)", async () => {
    const { link } = setup({ account: noPlayer, guest: GUEST, link: new ApiError(409, "identity_already_linked") });
    expect(await link.link()).toEqual({ kind: "conflict" });
  });

  it("offers a retry of the link for auth_unavailable and rate_limited, with Retry-After", async () => {
    for (const error of [new ApiError(503, "auth_unavailable", null, 30), new ApiError(429, "rate_limited", null, 12)]) {
      const { link } = setup({ account: noPlayer, guest: GUEST, link: error });
      const outcome = await link.link();
      expect(outcome).toMatchObject({ kind: "retry", step: "link", retryAfterSeconds: error.retryAfterSeconds });
    }
  });

  it("rejects an unconfirmed identity (422 invalid_link_proof); the guest stays valid", async () => {
    const { link } = setup({ account: noPlayer, guest: GUEST, link: new ApiError(422, "invalid_link_proof") });
    expect((await link.link()).kind).toBe("rejected");
  });
});
