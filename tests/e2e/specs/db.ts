import type { Page } from "@playwright/test";
import pg from "pg";

/** PostgreSQL with `services/api/db/schema.sql` applied; the API under test uses the same database. */
export const DATABASE_URL = process.env["E2E_DATABASE_URL"] ?? `postgres://vybe:vybe@localhost:${process.env["E2E_PG_PORT"] ?? "55432"}/vybe`;

/** The calling player's id, from the API with the page's guest cookie. */
export async function playerId(page: Page): Promise<string> {
  const response = await page.request.get("/api/v1/me");
  if (!response.ok()) throw new Error(`GET /me answered ${response.status()}`);
  return ((await response.json()) as { id: string }).id;
}

/**
 * Test data: gives a player V-Coins without 60 s game runs. It writes an `admin_adjustment` ledger row
 * and the wallet balance in one transaction, as the API does (spec §4: the ledger explains the balance).
 */
export async function grantCoins(page: Page, amount: number): Promise<void> {
  const id = await playerId(page);
  const client = new pg.Client({ connectionString: DATABASE_URL });
  await client.connect();
  try {
    await client.query("BEGIN");
    const { rows } = await client.query<{ balance: string }>(
      "UPDATE vybe.wallets SET balance = balance + $2 WHERE player_id = $1 RETURNING balance",
      [id, amount],
    );
    const reference = crypto.randomUUID();
    await client.query(
      `INSERT INTO vybe.wallet_transactions (id, player_id, delta, balance_after, reason, reference_id, ledger_key)
       VALUES ($1, $2, $3, $4, 'admin_adjustment', $5, $6)`,
      [crypto.randomUUID(), id, amount, rows[0]?.balance, reference, `admin:${reference}`],
    );
    await client.query("COMMIT");
  } catch (error: unknown) {
    await client.query("ROLLBACK");
    throw error;
  } finally {
    await client.end();
  }
}
