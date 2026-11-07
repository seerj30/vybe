import type { Schemas } from "@vybe/contracts";
import type { SocialApi } from "../api/social-api";

export const REPORT_REASONS: readonly { value: Schemas["ReportReason"]; label: string }[] = [
  { value: "offensive_name", label: "Offensive name" },
  { value: "cheating", label: "Cheating" },
  { value: "harassment", label: "Harassment" },
  { value: "other", label: "Something else" },
];

/** Upper bound on pages read; a player with more blocks sees the "Block" button, which is idempotent. */
const MAX_PAGES = 10;

/** True when the caller blocked `handle`. The API never tells who blocked the caller. */
export async function isBlocked(api: Pick<SocialApi, "listBlocks">, handle: string): Promise<boolean> {
  let cursor: string | undefined;
  for (let page = 0; page < MAX_PAGES; page++) {
    const result = await api.listBlocks({ cursor, limit: 100 });
    if (result.items.some((blocked) => blocked.handle === handle)) return true;
    if (result.nextCursor === null) return false;
    cursor = result.nextCursor;
  }
  return false;
}
