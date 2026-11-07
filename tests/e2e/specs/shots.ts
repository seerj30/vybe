import type { Page } from "@playwright/test";
import pg from "pg";
import { generateWaypoints, targetAt, type ShotEvent, type StreetHoopsConfig } from "../../../packages/game-rules/src/index.ts";
import { DATABASE_URL } from "./db.ts";

/**
 * Test data for rewards (spec v1.4 §7). The browser plays the run; before the submit leaves the page, this
 * rewrites the aim and power of every shot and keeps each shot's `tMs`:
 * - `aimed`: exactly at the basket (a skilled player: score ≥ 500, so the run earns coins);
 * - `missed`: far from the basket (score 0).
 * The server still checks the whole trace; only the shot values change. Call it before the run starts.
 */
export async function rewriteShots(page: Page, mode: "aimed" | "missed"): Promise<void> {
  let seed: number | undefined;
  page.on("response", async (response) => {
    if (/\/api\/v1\/games\/sessions\/[^/]+\/start$/.test(new URL(response.url()).pathname) && response.ok()) {
      seed = ((await response.json()) as { seed: number }).seed;
    }
  });
  await page.route("**/api/v1/games/sessions/*/submit", async (route) => {
    const body = JSON.parse(route.request().postData() ?? "{}") as { rulesetId: string; events: ShotEvent[] };
    if (seed === undefined) throw new Error("The start response was not seen");
    const ruleset = await page.request.get(`/api/v1/games/rulesets/${body.rulesetId}`);
    const { config } = (await ruleset.json()) as { config: StreetHoopsConfig };
    const waypoints = generateWaypoints(config, seed);
    const events = body.events.map(({ tMs }) => {
      if (mode === "missed") return { tMs, aimX: 1000, power: 0 };
      const { targetX, idealPower } = targetAt(config, waypoints, tMs);
      return { tMs, aimX: targetX, power: idealPower };
    });
    await route.fallback({ postData: JSON.stringify({ ...body, events }) });
  });
}

/**
 * Test data: moves the start of an active run back by `ms`, as if the run had started earlier.
 * A reward-eligible run needs `submittedAt − startedAt ≥ minRunMsForRewards` (58 s); this keeps API-made
 * and clock-controlled runs fast. `submit_deadline` does not change, so the run does not expire.
 */
export async function backdateRunStart(sessionId: string, ms: number): Promise<void> {
  const client = new pg.Client({ connectionString: DATABASE_URL });
  await client.connect();
  try {
    const result = await client.query(
      "UPDATE vybe.game_sessions SET started_at = started_at - make_interval(secs => $2::double precision / 1000) WHERE id = $1 AND status = 'active'",
      [sessionId, ms],
    );
    if (result.rowCount !== 1) throw new Error(`Session ${sessionId} is not active`);
  } finally {
    await client.end();
  }
}
