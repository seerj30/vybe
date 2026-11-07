import "@angular/compiler";
import { describe, expect, it } from "vitest";
import { safeDownloadUrl } from "./data-export";

describe("safeDownloadUrl", () => {
  it("keeps https links", () => {
    expect(safeDownloadUrl("https://storage.example.com/x?sig=1", "vybe.app")).toBe("https://storage.example.com/x?sig=1");
  });

  it("refuses script, data and http links outside a local host", () => {
    expect(safeDownloadUrl("javascript:alert(1)", "vybe.app")).toBeNull();
    expect(safeDownloadUrl("data:text/html,x", "vybe.app")).toBeNull();
    expect(safeDownloadUrl("http://storage.example.com/x", "vybe.app")).toBeNull();
    expect(safeDownloadUrl("file:///tmp/x.json", "vybe.app")).toBeNull();
    expect(safeDownloadUrl(null, "vybe.app")).toBeNull();
  });

  it("allows the dev storage (file:) on localhost", () => {
    expect(safeDownloadUrl("file:///tmp/x.json", "localhost")).toBe("file:///tmp/x.json");
  });
});
