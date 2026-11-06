import type { Problem, ProblemCode, Schemas } from "@vybe/contracts";

/** A contract problem code, or a client-side failure that never reached a problem response. */
export type ErrorCode = ProblemCode | "network_error" | "unexpected_response";

const UNSAFE_METHODS = new Set(["POST", "PUT", "PATCH", "DELETE"]);
/** Codes whose request can be sent again with the same Idempotency-Key (spec §2 J1, §4). */
const RETRYABLE_CODES = new Set<ErrorCode>(["network_error", "request_in_progress", "rate_limited", "internal_error"]);

export class ApiError extends Error {
  constructor(
    readonly status: number,
    readonly code: ErrorCode,
    readonly problem: Problem | null = null,
  ) {
    super(problem?.title ?? code);
    this.name = "ApiError";
  }

  /** True when the outcome is unknown or temporary, so the same action may be sent again. */
  get retryable(): boolean {
    return RETRYABLE_CODES.has(this.code) || this.status >= 500;
  }
}

export function isApiError(error: unknown, ...codes: ErrorCode[]): error is ApiError {
  return error instanceof ApiError && (codes.length === 0 || codes.includes(error.code));
}

export type Query = Record<string, string | number | undefined>;

export interface RequestOptions {
  body?: unknown;
  idempotencyKey?: string;
  query?: Query;
}

export interface ApiResponse<T> {
  status: number;
  data: T;
}

export type SubmitResponse = Schemas["SubmitResult"] | Schemas["SubmitAccepted"];

/**
 * Typed client for the VYBE API (contract: packages/contracts). The guest session is an HttpOnly
 * cookie, so every request sends same-origin credentials. Unsafe methods send the CSRF header.
 */
export class ApiClient {
  constructor(
    private readonly fetchFn: typeof fetch = (input, init) => fetch(input, init),
    private readonly baseUrl = "/api/v1",
    private readonly timeoutMs = 15_000,
  ) {}

  createGuest(): Promise<Schemas["GuestCreated"]> {
    return this.data("POST", "/guests");
  }

  getMe(): Promise<Schemas["Me"]> {
    return this.data("GET", "/me");
  }

  getAvatar(): Promise<Schemas["Avatar"]> {
    return this.data("GET", "/me/avatar");
  }

  updateAvatar(body: Schemas["AvatarUpdateRequest"]): Promise<Schemas["AvatarUpdated"]> {
    return this.data("PUT", "/me/avatar", { body });
  }

  listCatalog(query: { slot?: Schemas["ItemSlot"]; cursor?: string; limit?: number } = {}): Promise<Schemas["CatalogPage"]> {
    return this.data("GET", "/catalog/items", { query });
  }

  purchase(body: Schemas["PurchaseRequest"], idempotencyKey: string): Promise<Schemas["Purchase"]> {
    return this.data("POST", "/purchases", { body, idempotencyKey });
  }

  getInventory(): Promise<Schemas["Inventory"]> {
    return this.data("GET", "/me/inventory");
  }

  equip(body: Schemas["EquipRequest"]): Promise<Schemas["EquipmentUpdated"]> {
    return this.data("POST", "/me/equipment", { body });
  }

  listWalletTransactions(query: { cursor?: string; limit?: number } = {}): Promise<Schemas["LedgerPage"]> {
    return this.data("GET", "/me/wallet/transactions", { query });
  }

  getRuleset(rulesetId: string): Promise<Schemas["Ruleset"]> {
    return this.data("GET", `/games/rulesets/${segment(rulesetId)}`);
  }

  createStreetHoopsSession(body: Schemas["SessionCreateRequest"] = {}): Promise<Schemas["SessionCreated"]> {
    return this.data("POST", "/games/street-hoops/sessions", { body });
  }

  startSession(sessionId: string): Promise<Schemas["SessionStarted"]> {
    return this.data("POST", `/games/sessions/${segment(sessionId)}/start`);
  }

  /** 200 is the final outcome; 202 means "poll `getSession`". */
  submitSession(sessionId: string, body: Schemas["SubmitRequest"], idempotencyKey: string): Promise<SubmitResponse> {
    return this.data("POST", `/games/sessions/${segment(sessionId)}/submit`, { body, idempotencyKey });
  }

  getSession(sessionId: string): Promise<Schemas["GameSession"]> {
    return this.data("GET", `/games/sessions/${segment(sessionId)}`);
  }

  async request<T>(method: string, path: string, options: RequestOptions = {}): Promise<ApiResponse<T>> {
    const headers: Record<string, string> = { Accept: "application/json" };
    if (UNSAFE_METHODS.has(method)) headers["X-Requested-With"] = "vybe-web";
    if (options.body !== undefined) headers["Content-Type"] = "application/json";
    if (options.idempotencyKey !== undefined) headers["Idempotency-Key"] = options.idempotencyKey;

    let response: Response;
    try {
      response = await this.fetchFn(this.baseUrl + path + queryString(options.query), {
        method,
        headers,
        body: options.body === undefined ? undefined : JSON.stringify(options.body),
        credentials: "same-origin",
        signal: AbortSignal.timeout(this.timeoutMs),
      });
    } catch {
      // Offline, DNS, reset or timeout: the server may or may not have applied the request.
      throw new ApiError(0, "network_error");
    }

    if (!response.ok) throw await problemError(response);
    if (response.status === 204) return { status: response.status, data: undefined as T };
    try {
      return { status: response.status, data: (await response.json()) as T };
    } catch {
      // The body was cut (timeout, reset) or is not JSON: the server may have applied the request.
      throw new ApiError(0, "network_error");
    }
  }

  private async data<T>(method: string, path: string, options?: RequestOptions): Promise<T> {
    return (await this.request<T>(method, path, options)).data;
  }
}

/** Fresh key for one user action; reuse it for every retry of that action. */
export function newIdempotencyKey(): string {
  return crypto.randomUUID();
}

/**
 * Idempotency keys per pending user action (for example one purchase per item). A key lives until the
 * action has a known outcome, so a retry after a network failure sends the same key (spec §2 J1, §4).
 */
export class ActionKeys<K> {
  private readonly keys = new Map<K, string>();

  keyFor(action: K): string {
    let key = this.keys.get(action);
    if (key === undefined) {
      key = newIdempotencyKey();
      this.keys.set(action, key);
    }
    return key;
  }

  /** Forgets the key when the outcome is known: success, or an error that is not retryable. */
  settle(action: K, error?: unknown): void {
    if (error instanceof ApiError && error.retryable) return;
    this.keys.delete(action);
  }
}

/** Encodes a path segment taken from data (ids, handles). */
export function segment(value: string): string {
  return encodeURIComponent(value);
}

function queryString(query: Query | undefined): string {
  if (query === undefined) return "";
  const params = new URLSearchParams();
  for (const [key, value] of Object.entries(query)) {
    if (value !== undefined) params.set(key, String(value));
  }
  const text = params.toString();
  return text === "" ? "" : `?${text}`;
}

async function problemError(response: Response): Promise<ApiError> {
  let body: unknown = null;
  try {
    body = await response.json();
  } catch {
    // Not JSON: a proxy or gateway error page.
  }
  if (isProblem(body)) return new ApiError(response.status, body.code, body);
  return new ApiError(response.status, response.status >= 500 ? "internal_error" : "unexpected_response");
}

function isProblem(body: unknown): body is Problem {
  return typeof body === "object" && body !== null && typeof (body as { code?: unknown }).code === "string";
}
