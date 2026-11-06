import { describe, expect, it } from "vitest";
import { ApiError } from "./api-client";
import { errorMessage } from "./messages";

describe("errorMessage", () => {
  it("maps contract codes to player text", () => {
    expect(errorMessage(new ApiError(0, "network_error"))).toMatch(/No connection/);
    expect(errorMessage(new ApiError(409, "revision_conflict"))).toMatch(/another tab/);
  });

  it("uses the generic text for unknown codes and non-API errors", () => {
    expect(errorMessage(new ApiError(422, "validation_failed"))).toBe("Something went wrong. Try again.");
    expect(errorMessage(new Error("boom"))).toBe("Something went wrong. Try again.");
  });
});
