import type { APIRequestContext, Browser, Page } from "@playwright/test";
import pg from "pg";
import { expect, newPlayer } from "./app.ts";
import { DATABASE_URL, playerId } from "./db.ts";

const UNSAFE = { "content-type": "application/json", "x-requested-with": "vybe-web" };

interface Me {
  id: string;
  handle: string;
}

export async function me(request: APIRequestContext): Promise<Me> {
  const response = await request.get("/api/v1/me");
  expect(response.status()).toBe(200);
  return (await response.json()) as Me;
}

/** A second player in its own browser context (its own guest cookie). The caller closes the context. */
export async function otherPlayer(browser: Browser): Promise<Page> {
  const context = await browser.newContext();
  const page = await context.newPage();
  await newPlayer(page);
  return page;
}

/**
 * Test data: a verified, challenge-eligible run without 60 s of play: `minShotsForRewards` shots
 * `minGapMs` apart, submitted when the claimed game time is inside `earlyToleranceMs` (spec §6).
 * With `challengeId`, it is the recipient run of an accepted challenge.
 */
export async function verifiedRun(request: APIRequestContext, challengeId?: string): Promise<string> {
  const created = await request.post("/api/v1/games/street-hoops/sessions", { headers: UNSAFE, data: challengeId === undefined ? {} : { challengeId } });
  expect(created.status()).toBe(201);
  const { sessionId, rulesetId } = (await created.json()) as { sessionId: string; rulesetId: string };
  const { config } = (await (await request.get(`/api/v1/games/rulesets/${rulesetId}`)).json()) as {
    config: { minShotsForRewards: number; minGapMs: number; earlyToleranceMs: number };
  };
  const events = Array.from({ length: config.minShotsForRewards }, (_, i) => ({ tMs: i * config.minGapMs, aimX: 0, power: 600 }));
  const claimedMs = events[events.length - 1]?.tMs ?? 0;
  expect((await request.post(`/api/v1/games/sessions/${sessionId}/start`, { headers: UNSAFE })).status()).toBe(200);
  await new Promise((resolve) => setTimeout(resolve, Math.max(0, claimedMs - config.earlyToleranceMs) + 500));
  const submitted = await request.post(`/api/v1/games/sessions/${sessionId}/submit`, {
    headers: { ...UNSAFE, "idempotency-key": crypto.randomUUID() },
    data: { rulesetId, events },
  });
  expect(submitted.status()).toBe(200);
  expect(await submitted.json()).toMatchObject({ status: "verified" });
  return sessionId;
}

export interface CreatedChallenge {
  challengeId: string;
  token: string;
}

/** Creates a challenge from a fresh verified run of the request's player. */
export async function createChallenge(request: APIRequestContext, recipientHandle?: string): Promise<CreatedChallenge> {
  const creatorSessionId = await verifiedRun(request);
  const response = await request.post("/api/v1/challenges", {
    headers: { ...UNSAFE, "idempotency-key": crypto.randomUUID() },
    data: recipientHandle === undefined ? { creatorSessionId } : { creatorSessionId, recipientHandle },
  });
  expect(response.status()).toBe(201);
  const { challengeId, shareUrl } = (await response.json()) as { challengeId: string; shareUrl: string };
  return { challengeId, token: tokenOf(shareUrl) };
}

/** The invite token of a share link; the API's ShareBaseUrl is not this origin in tests. */
export function tokenOf(shareUrl: string): string {
  const token = /\/challenge\/([A-Za-z0-9_-]+)$/.exec(new URL(shareUrl).pathname)?.[1];
  if (token === undefined) throw new Error(`Not a share link: ${shareUrl}`);
  return token;
}

export async function acceptStatus(request: APIRequestContext, token: string): Promise<{ status: number; code?: string }> {
  const response = await request.post(`/api/v1/invites/${token}/accept`, {
    headers: { ...UNSAFE, "idempotency-key": crypto.randomUUID() },
  });
  const body = (await response.json()) as { code?: string };
  return { status: response.status(), code: body.code };
}

/** Test data: the request's player accepts the invite and plays a verified recipient run, so the challenge completes. */
export async function completeAsRecipient(request: APIRequestContext, challenge: CreatedChallenge): Promise<void> {
  expect((await acceptStatus(request, challenge.token)).status).toBe(200);
  await verifiedRun(request, challenge.challengeId);
}

/**
 * Fault injection: the server applies the first matching request, but its answer never reaches the
 * page (spec §2 J1). Returns the Idempotency-Key of every matching request, in order.
 */
export async function loseFirstAnswer(page: Page, url: string): Promise<string[]> {
  const keys: string[] = [];
  await page.route(url, async (route) => {
    const request = route.request();
    if (request.method() !== "POST") return route.continue();
    keys.push((await request.allHeaders())["idempotency-key"] ?? "");
    if (keys.length > 1) return route.continue();
    await route.fetch();
    await route.abort("connectionreset");
  });
  return keys;
}

async function withDb<T>(work: (client: pg.Client) => Promise<T>): Promise<T> {
  const client = new pg.Client({ connectionString: DATABASE_URL });
  await client.connect();
  try {
    return await work(client);
  } finally {
    await client.end();
  }
}

/** Test data: moves the accept deadline into the past, as if 24 hours had passed (spec §2 J2). */
export async function expireInvite(challengeId: string): Promise<void> {
  await withDb((db) => db.query("UPDATE vybe.challenges SET accept_deadline = now() - interval '1 second' WHERE id = $1", [challengeId]));
}

/** Test data: completes today's quest `code` for the page's player, as verified runs would (spec §8). */
export async function completeQuest(page: Page, code: string): Promise<void> {
  // GET /me/quests creates today's rows first.
  expect((await page.request.get("/api/v1/me/quests")).status()).toBe(200);
  const id = await playerId(page);
  const result = await withDb((db) =>
    db.query(
      `UPDATE vybe.player_quests pq SET progress = pq.target, status = 'completed', completed_at = now()
       FROM vybe.quest_definitions qd
       WHERE pq.quest_id = qd.id AND qd.code = $2 AND pq.player_id = $1 AND pq.status = 'active'`,
      [id, code],
    ),
  );
  expect(result.rowCount).toBe(1);
}

/** Test data: the player loses an item, so a saved outfit with it is no longer valid. */
export async function removeOwnedItem(page: Page, itemName: string): Promise<void> {
  const id = await playerId(page);
  const result = await withDb((db) =>
    db.query(
      `DELETE FROM vybe.player_inventory pi USING vybe.catalog_items ci
       WHERE pi.item_id = ci.id AND pi.player_id = $1 AND ci.name = $2`,
      [id, itemName],
    ),
  );
  expect(result.rowCount).toBe(1);
}

export async function reportCount(reporter: Page, target: Page): Promise<number> {
  const [from, to] = [await playerId(reporter), await playerId(target)];
  const { rows } = await withDb((db) =>
    db.query<{ n: string }>("SELECT count(*) AS n FROM vybe.player_reports WHERE reporter_player_id = $1 AND target_player_id = $2", [from, to]),
  );
  return Number(rows[0]?.n ?? 0);
}
