import { describe, expect, it } from "vitest";
import { ApiClient } from "./api-client";
import { SocialApi } from "./social-api";

interface Call {
  url: string;
  init: RequestInit;
}

function client(status = 200, body: unknown = {}): { social: SocialApi; calls: Call[] } {
  const calls: Call[] = [];
  const fetchFn = (input: RequestInfo | URL, init?: RequestInit): Promise<Response> => {
    calls.push({ url: String(input), init: init ?? {} });
    const response = status === 204 ? new Response(null, { status }) : new Response(JSON.stringify(body), { status });
    return Promise.resolve(response);
  };
  return { social: new SocialApi(new ApiClient(fetchFn as typeof fetch)), calls };
}

function headers(call: Call | undefined): Record<string, string> {
  return (call?.init.headers ?? {}) as Record<string, string>;
}

describe("SocialApi", () => {
  it("accepts an invite with the key and an encoded token", async () => {
    const { social, calls } = client(200, { challengeId: "c", status: "accepted", completeDeadline: "t" });
    await social.acceptInvite("a/b", "key-1");
    expect(calls[0]?.url).toBe("/api/v1/invites/a%2Fb/accept");
    expect(calls[0]?.init.method).toBe("POST");
    expect(headers(calls[0])["Idempotency-Key"]).toBe("key-1");
    expect(headers(calls[0])["X-Requested-With"]).toBe("vybe-web");
  });

  it("creates a profile challenge with the recipient handle", async () => {
    const { social, calls } = client(201, { challengeId: "c", shareUrl: "https://x/challenge/t", acceptDeadline: "t" });
    await social.createChallenge({ creatorSessionId: "s", recipientHandle: "bo" }, "key-2");
    expect(calls[0]?.url).toBe("/api/v1/challenges");
    expect(JSON.parse(String(calls[0]?.init.body))).toEqual({ creatorSessionId: "s", recipientHandle: "bo" });
  });

  it("blocks with a body and unblocks by handle", async () => {
    const { social, calls } = client(204);
    await social.block("bo");
    await social.unblock("bo");
    expect(JSON.parse(String(calls[0]?.init.body))).toEqual({ handle: "bo" });
    expect(calls[1]?.url).toBe("/api/v1/me/blocks/bo");
    expect(calls[1]?.init.method).toBe("DELETE");
  });

  it("applies an outfit with the avatar revision", async () => {
    const { social, calls } = client(200, { revision: 4, equipped: {} });
    await social.applyOutfit("o1", 3);
    expect(calls[0]?.url).toBe("/api/v1/me/outfits/o1/apply");
    expect(JSON.parse(String(calls[0]?.init.body))).toEqual({ expectedAvatarRevision: 3 });
  });
});
