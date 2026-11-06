/**
 * Ruleset checksum (spec v1.3 §4): lowercase hex SHA-256 of the RFC 8785
 * canonical JSON of `config`.
 *
 * Supported subset: objects, arrays, safe integers and ASCII strings that need
 * no escaping. Ruleset configs use only this subset; anything else throws, so
 * the C# and TS implementations cannot silently disagree.
 */
const SAFE_STRING = /^[\x20-\x21\x23-\x5b\x5d-\x7e]*$/;

export function canonicalJson(value: unknown): string {
  if (typeof value === "number") {
    if (!Number.isSafeInteger(value)) {
      throw new TypeError(`canonicalJson supports only safe integers, got ${value}`);
    }
    return String(value);
  }
  if (typeof value === "string") {
    if (!SAFE_STRING.test(value)) {
      throw new TypeError("canonicalJson supports only ASCII strings without escapes");
    }
    return `"${value}"`;
  }
  if (Array.isArray(value)) {
    return `[${value.map(canonicalJson).join(",")}]`;
  }
  if (value !== null && typeof value === "object") {
    // RFC 8785 sorts keys by UTF-16 code units; default sort does exactly that.
    const keys = Object.keys(value).sort();
    const record = value as Record<string, unknown>;
    return `{${keys.map((k) => `${canonicalJson(k)}:${canonicalJson(record[k])}`).join(",")}}`;
  }
  throw new TypeError(`canonicalJson does not support ${value === null ? "null" : typeof value}`);
}

export async function rulesetChecksum(config: unknown): Promise<string> {
  const bytes = new TextEncoder().encode(canonicalJson(config));
  const digest = await globalThis.crypto.subtle.digest("SHA-256", bytes);
  return Array.from(new Uint8Array(digest), (b) => b.toString(16).padStart(2, "0")).join("");
}
