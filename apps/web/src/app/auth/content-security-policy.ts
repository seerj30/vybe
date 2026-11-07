/**
 * Content Security Policy of the web app (spec §11). The Supabase origin comes from the runtime config,
 * so the app adds the policy as a `<meta>` element at start-up, before it renders API data. A policy can
 * only get stricter after that: a second meta element cannot loosen it.
 * `frame-ancestors` works only as an HTTP header; the hosting sends it (apps/web/README.md).
 */
export function contentSecurityPolicy(supabaseUrl: string | null): string {
  return [
    "default-src 'self'",
    "script-src 'self'",
    // Angular adds component styles as <style> elements at run time.
    "style-src 'self' 'unsafe-inline'",
    // Item pictures come from the asset CDN (spec §9); Three.js textures can be data or blob URLs.
    "img-src 'self' data: blob: https:",
    `connect-src 'self'${supabaseUrl === null ? "" : ` ${supabaseUrl}`}`,
    "object-src 'none'",
    "base-uri 'self'",
    "form-action 'self'",
  ].join("; ");
}

/** Adds the policy once; later calls change nothing. */
export function applyContentSecurityPolicy(supabaseUrl: string | null, doc: Document | null = typeof document === "undefined" ? null : document): void {
  if (doc === null || doc.querySelector('meta[http-equiv="Content-Security-Policy"]') !== null) return;
  const meta = doc.createElement("meta");
  meta.httpEquiv = "Content-Security-Policy";
  meta.content = contentSecurityPolicy(supabaseUrl);
  doc.head.prepend(meta);
}
