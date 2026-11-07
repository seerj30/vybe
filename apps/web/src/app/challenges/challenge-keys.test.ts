import { describe, expect, it } from "vitest";
import { challengeKey, forgetChallengeKey, keyName } from "./challenge-keys";

function memoryStorage(): Pick<Storage, "getItem" | "setItem" | "removeItem"> {
  const items = new Map<string, string>();
  return {
    getItem: (name) => items.get(name) ?? null,
    setItem: (name, value) => void items.set(name, value),
    removeItem: (name) => void items.delete(name),
  };
}

describe("challenge create keys", () => {
  it("reuses the key for the same request (reload, retry)", () => {
    const storage = memoryStorage();
    const first = challengeKey({ sessionId: "s1", to: "Bo" }, storage);
    expect(challengeKey({ sessionId: "s1", to: "bo" }, storage)).toBe(first);
  });

  it("uses another key when the body changes (recipient or rematch)", () => {
    const storage = memoryStorage();
    const plain = challengeKey({ sessionId: "s1" }, storage);
    expect(challengeKey({ sessionId: "s1", to: "bo" }, storage)).not.toBe(plain);
    expect(challengeKey({ sessionId: "s1", rematchOf: "c1" }, storage)).not.toBe(plain);
    expect(keyName({ sessionId: "s1" })).not.toBe(keyName({ sessionId: "s1", rematchOf: "c1" }));
  });

  it("gives a fresh key after a known failure", () => {
    const storage = memoryStorage();
    const first = challengeKey({ sessionId: "s1" }, storage);
    forgetChallengeKey({ sessionId: "s1" }, storage);
    expect(challengeKey({ sessionId: "s1" }, storage)).not.toBe(first);
  });

  it("still gives a key when storage is blocked", () => {
    const blocked = {
      getItem: () => {
        throw new Error("blocked");
      },
      setItem: () => undefined,
      removeItem: () => undefined,
    };
    expect(challengeKey({ sessionId: "s1" }, blocked)).toMatch(/^[0-9a-f-]{36}$/);
  });
});
