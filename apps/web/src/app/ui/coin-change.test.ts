import { describe, expect, it } from "vitest";
import { coinChangeKind, signedCoins } from "./coin-change";

describe("coin change", () => {
  it("marks earned and spent amounts", () => {
    expect(coinChangeKind(100)).toBe("earned");
    expect(coinChangeKind(-350)).toBe("spent");
    expect(coinChangeKind(0)).toBe("none");
  });

  it("keeps the sign in the text, so color is not the only signal", () => {
    expect(signedCoins(100)).toBe("+100");
    expect(signedCoins(-350)).toBe("−350");
    expect(signedCoins(0)).toBe("0");
    expect(signedCoins(-1500)).toBe("−1500");
  });
});
