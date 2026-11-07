import { describe, expect, it } from "vitest";
import { isBlocked } from "./safety";

const entry = (handle: string) => ({ handle, displayName: handle, blockedAt: "2026-11-06T10:00:00Z" });

describe("isBlocked", () => {
  it("reads the pages until it finds the handle", async () => {
    const cursors: (string | undefined)[] = [];
    const pages = [
      { items: [entry("ana")], nextCursor: "p2" },
      { items: [entry("bo")], nextCursor: null },
    ];
    const api = {
      listBlocks: (query: { cursor?: string }) => {
        cursors.push(query.cursor);
        return Promise.resolve(pages[cursors.length - 1]!);
      },
    };
    expect(await isBlocked(api, "bo")).toBe(true);
    expect(cursors).toEqual([undefined, "p2"]);
  });

  it("is false when the list ends without the handle", async () => {
    const api = { listBlocks: () => Promise.resolve({ items: [entry("ana")], nextCursor: null }) };
    expect(await isBlocked(api, "bo")).toBe(false);
  });
});
