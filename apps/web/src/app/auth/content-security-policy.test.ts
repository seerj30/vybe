import { describe, expect, it } from "vitest";
import { contentSecurityPolicy } from "./content-security-policy";

describe("contentSecurityPolicy", () => {
  it("allows connections only to the app and the configured Supabase origin", () => {
    const policy = contentSecurityPolicy("https://abc.supabase.co");
    expect(policy).toContain("connect-src 'self' https://abc.supabase.co");
    expect(policy).toContain("default-src 'self'");
    expect(policy).toContain("object-src 'none'");
    expect(policy).toContain("base-uri 'self'");
    expect(policy).not.toContain("unsafe-eval");
  });

  it("allows only the app's own origin when sign-in is off", () => {
    expect(contentSecurityPolicy(null)).toContain("connect-src 'self';");
  });
});
