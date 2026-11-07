import { describe, expect, it } from "vitest";
import { loadAuthConfig, parseAuthConfig } from "./runtime-config";

function respond(status: number, body: string, type = "application/json"): typeof fetch {
  return (() => Promise.resolve(new Response(body, { status, headers: { "Content-Type": type } }))) as typeof fetch;
}

describe("runtime config", () => {
  it("accepts an https Supabase URL and keeps only its origin", () => {
    expect(parseAuthConfig({ supabaseUrl: "https://abc.supabase.co/", supabaseAnonKey: "k" })).toEqual({
      supabaseUrl: "https://abc.supabase.co",
      supabaseAnonKey: "k",
    });
  });

  it("accepts http only for a local host", () => {
    expect(parseAuthConfig({ supabaseUrl: "http://localhost:5290", supabaseAnonKey: "k" })).not.toBeNull();
    expect(parseAuthConfig({ supabaseUrl: "http://auth.example.com", supabaseAnonKey: "k" })).toBeNull();
    expect(parseAuthConfig({ supabaseUrl: "javascript:alert(1)", supabaseAnonKey: "k" })).toBeNull();
  });

  it("rejects a missing key or a wrong shape", () => {
    expect(parseAuthConfig({ supabaseUrl: "https://abc.supabase.co", supabaseAnonKey: "" })).toBeNull();
    expect(parseAuthConfig({ supabaseUrl: 1, supabaseAnonKey: "k" })).toBeNull();
    expect(parseAuthConfig(null)).toBeNull();
  });

  it("turns sign-in off without a config file or with an HTML fallback page", async () => {
    await expect(loadAuthConfig(respond(404, ""))).resolves.toBeNull();
    await expect(loadAuthConfig(respond(200, "<!doctype html>", "text/html"))).resolves.toBeNull();
  });

  it("fails on a server error or an invalid file, so the app can show 'unavailable'", async () => {
    await expect(loadAuthConfig(respond(500, "{}"))).rejects.toThrow();
    await expect(loadAuthConfig(respond(200, '{"supabaseUrl":"http://evil.example"}'))).rejects.toThrow();
  });
});
