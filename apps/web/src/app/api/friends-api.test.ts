import { describe, expect, it } from "vitest";
import { ApiClient } from "./api-client";
import { FriendsApi } from "./friends-api";

interface Call {
  url: string;
  init: RequestInit;
}

function client(status = 200, body: unknown = {}): { friends: FriendsApi; calls: Call[] } {
  const calls: Call[] = [];
  const fetchFn = (input: RequestInfo | URL, init?: RequestInit): Promise<Response> => {
    calls.push({ url: String(input), init: init ?? {} });
    return Promise.resolve(status === 204 ? new Response(null, { status }) : new Response(JSON.stringify(body), { status }));
  };
  return { friends: new FriendsApi(new ApiClient(fetchFn as typeof fetch)), calls };
}

describe("FriendsApi", () => {
  it("sends a request with the handle in the body and the CSRF header", async () => {
    const { friends, calls } = client(200, { handle: "bo", status: "pending" });
    await friends.sendRequest("Bo");
    expect(calls[0]?.url).toBe("/api/v1/me/friend-requests");
    expect(calls[0]?.init.method).toBe("POST");
    expect(JSON.parse(String(calls[0]?.init.body))).toEqual({ handle: "Bo" });
    expect((calls[0]?.init.headers as Record<string, string>)["X-Requested-With"]).toBe("vybe-web");
  });

  it("encodes the handle in paths", async () => {
    const { friends, calls } = client(204);
    await friends.cancel("a/b");
    await friends.remove("a/b");
    expect(calls.map((c) => [c.init.method, c.url])).toEqual([
      ["DELETE", "/api/v1/me/friend-requests/a%2Fb"],
      ["DELETE", "/api/v1/me/friends/a%2Fb"],
    ]);
  });

  it("asks for a board by game, scope and week", async () => {
    const { friends, calls } = client(200, {});
    await friends.weeklyLeaderboard("street-hoops", { scope: "friends", week: "2026-W46" });
    expect(calls[0]?.url).toBe("/api/v1/leaderboards/street-hoops/weekly?scope=friends&week=2026-W46");
  });
});
