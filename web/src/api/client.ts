/**
 * HTTP client for the Paybitch API.
 *
 * Owns three things the rest of the app should never think about: attaching the
 * bearer token, refreshing it exactly once when it expires, and turning RFC 9457
 * problem responses into typed errors.
 */

const BASE_URL: string =
  (import.meta.env.VITE_API_URL as string | undefined) ?? "http://localhost:5234/v1";

/** Where the session lives between reloads. */
const REFRESH_KEY = "paybitch.refreshToken";

export interface Session {
  accessToken: string;
  refreshToken: string;
  /** Epoch ms after which the access token is no longer accepted. */
  expiresAt: number;
  user: { id: string; displayName: string | null; email: string | null };
}

/**
 * An error carrying the server's problem `code`.
 *
 * The code is the part worth branching on — the title is human prose that may
 * change, the code is a closed set the API commits to.
 */
export class ApiError extends Error {
  readonly status: number;
  readonly code: string;
  /** Problem extensions, e.g. `existingId` on a client_id_conflict. */
  readonly extensions: Record<string, unknown>;

  constructor(
    status: number,
    code: string,
    message: string,
    extensions: Record<string, unknown> = {},
  ) {
    super(message);
    this.name = "ApiError";
    this.status = status;
    this.code = code;
    this.extensions = extensions;
  }

  /** The session is gone, not merely stale — the user has to sign in again. */
  get isUnauthenticated(): boolean {
    return this.status === 401;
  }
}

/* ───────────────────────────────────────────────────── session storage ── */

// The access token is deliberately memory-only: it is short-lived and keeping it
// out of storage limits what an XSS can lift. The refresh token has to survive a
// reload, and the API offers no cookie option, so localStorage is the only place
// it can go — a known trade-off of a bearer-only API rather than an oversight.
let session: Session | null = null;
let onSessionLost: (() => void) | null = null;

export function getSession(): Session | null {
  return session;
}

export function setSession(next: Session | null): void {
  session = next;
  try {
    if (next) localStorage.setItem(REFRESH_KEY, next.refreshToken);
    else localStorage.removeItem(REFRESH_KEY);
  } catch {
    // Private-mode Safari and blocked site data — the session still works for
    // this tab, it just will not survive a reload.
  }
}

export function storedRefreshToken(): string | null {
  try {
    return localStorage.getItem(REFRESH_KEY);
  } catch {
    return null;
  }
}

/** Called when the session cannot be recovered, so the app can show sign-in. */
export function setSessionLostHandler(handler: () => void): void {
  onSessionLost = handler;
}

/* ─────────────────────────────────────────────────────────── requests ── */

interface RequestOptions {
  method?: string;
  body?: unknown;
  /** Sent as `If-Match`; required by the API on most updates and deletes. */
  version?: number;
  /** Skips the bearer header and the refresh dance. */
  anonymous?: boolean;
  signal?: AbortSignal;
}

// A single shared refresh promise, so five concurrent 401s trigger one refresh
// rather than five — each of which would rotate the token and invalidate the
// others, ending with the whole session revoked for reuse.
let refreshInFlight: Promise<boolean> | null = null;

export async function request<T>(path: string, options: RequestOptions = {}): Promise<T> {
  const response = await send(path, options);

  if (response.status === 401 && !options.anonymous) {
    const refreshed = await refreshSession();
    if (refreshed) {
      const retried = await send(path, options);
      return handle<T>(retried);
    }
    setSession(null);
    onSessionLost?.();
  }

  return handle<T>(response);
}

async function send(path: string, options: RequestOptions): Promise<Response> {
  const headers: Record<string, string> = { Accept: "application/json" };

  if (options.body !== undefined) headers["Content-Type"] = "application/json";
  if (options.version !== undefined) headers["If-Match"] = `"${options.version}"`;
  if (!options.anonymous && session) {
    headers.Authorization = `Bearer ${session.accessToken}`;
  }

  return fetch(`${BASE_URL}${path}`, {
    method: options.method ?? "GET",
    headers,
    body: options.body === undefined ? undefined : JSON.stringify(options.body),
    signal: options.signal,
    // Bearer-only API with no cookie scheme, so credentials must stay omitted —
    // including them would require AllowCredentials on the server's CORS policy.
    credentials: "omit",
  });
}

async function handle<T>(response: Response): Promise<T> {
  if (response.status === 204) return undefined as T;

  const text = await response.text();
  const payload: unknown = text ? safeParse(text) : null;

  if (!response.ok) {
    const problem = (payload ?? {}) as Record<string, unknown>;
    const { type: _type, title, status: _status, detail, code, ...extensions } = problem;

    throw new ApiError(
      response.status,
      typeof code === "string" ? code : `http_${response.status}`,
      (typeof detail === "string" && detail) ||
        (typeof title === "string" && title) ||
        `Request failed with ${response.status}`,
      extensions,
    );
  }

  // The ETag is the concurrency token for the next write. It is only readable
  // because the server lists it in Access-Control-Expose-Headers.
  const etag = response.headers.get("ETag");
  if (etag && payload && typeof payload === "object") {
    (payload as Record<string, unknown>).version ??= parseETag(etag);
  }

  return payload as T;
}

function safeParse(text: string): unknown {
  try {
    return JSON.parse(text);
  } catch {
    return null;
  }
}

/** `W/"3"`, `"3"` and `3` all mean version 3. */
function parseETag(etag: string): number | undefined {
  const parsed = Number(etag.replace(/^W\//, "").replace(/"/g, "").trim());
  return Number.isFinite(parsed) ? parsed : undefined;
}

/* ──────────────────────────────────────────────────────────── refresh ── */

interface RefreshResponse {
  accessToken: string;
  refreshToken: string;
  expiresIn: number;
}

/**
 * Exchanges the refresh token for a new pair.
 *
 * The server rotates on every use and treats a replayed token as theft — it
 * kills the whole token family. That is why this is single-flight and why a
 * failure clears the session rather than retrying.
 */
async function refreshSession(): Promise<boolean> {
  if (refreshInFlight) return refreshInFlight;

  const token = session?.refreshToken ?? storedRefreshToken();
  if (!token) return false;

  refreshInFlight = (async () => {
    try {
      const response = await fetch(`${BASE_URL}/auth/refresh`, {
        method: "POST",
        headers: { "Content-Type": "application/json", Accept: "application/json" },
        body: JSON.stringify({ refreshToken: token }),
        credentials: "omit",
      });

      if (!response.ok) return false;

      const next = (await response.json()) as RefreshResponse;
      setSession({
        accessToken: next.accessToken,
        refreshToken: next.refreshToken,
        expiresAt: Date.now() + next.expiresIn * 1000,
        user: session?.user ?? { id: "", displayName: null, email: null },
      });
      return true;
    } catch {
      return false;
    } finally {
      refreshInFlight = null;
    }
  })();

  return refreshInFlight;
}

/** Restores a session from the stored refresh token on app start. */
export async function restoreSession(): Promise<boolean> {
  if (!storedRefreshToken()) return false;
  return refreshSession();
}

export const api = {
  get: <T>(path: string, options?: RequestOptions) =>
    request<T>(path, { ...options, method: "GET" }),
  post: <T>(path: string, body?: unknown, options?: RequestOptions) =>
    request<T>(path, { ...options, method: "POST", body }),
  patch: <T>(path: string, body?: unknown, options?: RequestOptions) =>
    request<T>(path, { ...options, method: "PATCH", body }),
  put: <T>(path: string, body?: unknown, options?: RequestOptions) =>
    request<T>(path, { ...options, method: "PUT", body }),
  delete: <T>(path: string, options?: RequestOptions) =>
    request<T>(path, { ...options, method: "DELETE" }),
};

export { BASE_URL };
