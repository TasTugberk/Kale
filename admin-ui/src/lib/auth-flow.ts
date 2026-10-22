import { needsRefresh, type AdminSession, type SessionStore } from "./sessions";

/**
 * The admin UI's sign-in decisions, kept free of Next.js and openid-client so they can be tested directly. The
 * routes in src/app pass in the real token exchange and admin API calls.
 */

/** The tokens AuthService hands out, in the shape the UI keeps them. */
export interface TokenSet {
  accessToken: string;
  refreshToken?: string;
  idToken?: string;
  expiresInSeconds: number;
}

export function sessionFromTokens(tokens: TokenSet, now: number): AdminSession {
  return {
    accessToken: tokens.accessToken,
    refreshToken: tokens.refreshToken,
    idToken: tokens.idToken,
    accessTokenExpiresAt: now + tokens.expiresInSeconds * 1000,
  };
}

// ----- Coming back from AuthService (/signin-oidc) -----

export type CallbackOutcome =
  | { kind: "signed-in"; sessionId: string }
  /** No auth.Admin. With an id token, AuthService's new session for "auth" should be ended too. */
  | { kind: "not-admin"; idToken?: string }
  /** Anything else (an error answer, no login in progress, a refused exchange): sign in again. */
  | { kind: "failed" };

export interface CallbackDeps {
  sessions: SessionStore;
  now: () => number;
  /** The code exchange; it also checks the state and the PKCE verifier, and throws when anything is off. */
  exchangeCode: (currentUrl: URL, verifier: string, state: string) => Promise<TokenSet>;
  /** Asks the admin API whether this token's session holds auth.Admin; throws when it can't tell (e.g. a 503). */
  checkAdmin: (accessToken: string) => Promise<"admin" | "not-admin">;
}

export async function completeSignIn(
  callbackUrl: URL, loginCookie: string | undefined, publicUrl: URL, deps: CallbackDeps,
): Promise<CallbackOutcome> {
  // AuthService refuses users without access to "auth" before any code is issued.
  const error = callbackUrl.searchParams.get("error");
  if (error === "access_denied") {
    return { kind: "not-admin" };
  }

  const login = readLoginCookie(loginCookie);
  if (error || !login) {
    return { kind: "failed" };
  }

  // As AuthService knows this page (the registered redirect address), not as the server sees itself.
  const currentUrl = new URL(`signin-oidc${callbackUrl.search}`, publicUrl);
  let tokens: TokenSet;
  try {
    tokens = await deps.exchangeCode(currentUrl, login.verifier, login.state);
  } catch {
    return { kind: "failed" };
  }

  // Signing in to "auth" doesn't make someone an admin: only holding auth.Admin does. When that can't be
  // checked right now, it's a failed sign-in (try again), never "not an admin", which would end their session.
  let check: "admin" | "not-admin";
  try {
    check = await deps.checkAdmin(tokens.accessToken);
  } catch {
    return { kind: "failed" };
  }

  if (check !== "admin") {
    return { kind: "not-admin", idToken: tokens.idToken };
  }

  return { kind: "signed-in", sessionId: deps.sessions.create(sessionFromTokens(tokens, deps.now())) };
}

function readLoginCookie(value: string | undefined): { verifier: string; state: string } | undefined {
  if (!value) {
    return undefined;
  }

  try {
    const parsed = JSON.parse(value) as { verifier?: unknown; state?: unknown };
    return typeof parsed.verifier === "string" && typeof parsed.state === "string"
      ? { verifier: parsed.verifier, state: parsed.state }
      : undefined;
  } catch {
    return undefined;
  }
}

// ----- Calling the admin API -----

/**
 * Calls AuthService's admin API with a session's access token, renewing it shortly before it expires. When the
 * session can't be used any more (renewal refused, 401 or 403 from AuthService because the session was stopped,
 * the admin signed in elsewhere or auth.Admin was taken away), the session is dropped and the admin has to sign
 * in again.
 */
export class AdminApiCaller {
  // Renewals in progress, per session: calls made at the same time share one, because AuthService accepts each
  // refresh token only once.
  private readonly renewals = new Map<string, Promise<AdminSession | undefined>>();

  constructor(
    private readonly sessions: SessionStore,
    private readonly now: () => number,
    private readonly refresh: (refreshToken: string) => Promise<TokenSet>,
  ) {}

  /**
   * "no-session": nobody is signed in here (or this UI restarted and forgot them); a normal sign-in.
   * "session-ended": AuthService refused the session (stopped, signed in elsewhere, rights taken away, expired);
   * the admin must enter the password again, or AuthService's own sign-in cookie would sign them straight back
   * in and end the other device's session in turn.
   */
  async call(
    sessionId: string | undefined, send: (accessToken: string) => Promise<Response>,
  ): Promise<Response | "no-session" | "session-ended"> {
    let session = sessionId ? this.sessions.get(sessionId) : undefined;
    if (!sessionId || !session) {
      return "no-session";
    }

    if (needsRefresh(session, this.now())) {
      session = await this.renewOnce(sessionId, session);
      if (!session) {
        return "session-ended";
      }
    }

    const response = await send(session.accessToken);
    if (response.status === 401 || response.status === 403) {
      this.sessions.delete(sessionId);
      return "session-ended";
    }

    return response;
  }

  private renewOnce(sessionId: string, session: AdminSession): Promise<AdminSession | undefined> {
    let renewal = this.renewals.get(sessionId);
    if (!renewal) {
      renewal = this.renew(sessionId, session).finally(() => this.renewals.delete(sessionId));
      this.renewals.set(sessionId, renewal);
    }

    return renewal;
  }

  private async renew(sessionId: string, session: AdminSession): Promise<AdminSession | undefined> {
    try {
      if (!session.refreshToken) {
        throw new Error("no refresh token");
      }

      const tokens = await this.refresh(session.refreshToken);
      const renewed = sessionFromTokens(tokens, this.now());

      // A refresh answer may leave out the refresh and id tokens; then the ones we have stay valid.
      renewed.refreshToken ??= session.refreshToken;
      renewed.idToken ??= session.idToken;
      this.sessions.update(sessionId, renewed);
      return renewed;
    } catch {
      // Refused (AuthService refuses once the session is no longer active) or AuthService unreachable: either
      // way the admin signs in again, which is simple and never keeps using a session that may have ended.
      this.sessions.delete(sessionId);
      return undefined;
    }
  }
}

// ----- Other sites -----

/**
 * Whether a request that changes something (a form post) comes from the admin UI's own pages. Browsers send
 * Origin with every POST; Sec-Fetch-Site covers the rest. Without either, refuse.
 */
export function isSameOrigin(headers: Headers, publicUrl: URL): boolean {
  const origin = headers.get("origin");
  if (origin) {
    return origin === publicUrl.origin;
  }

  return headers.get("sec-fetch-site") === "same-origin";
}

// ----- Loading once -----

/** Loads once and keeps the result; after a failure (e.g. AuthService was down), the next call tries again. */
export function cacheUntilFailure<T>(load: () => Promise<T>): () => Promise<T> {
  let cached: Promise<T> | undefined;
  return () => {
    cached ??= load().catch((error: unknown) => {
      cached = undefined;
      throw error;
    });
    return cached;
  };
}
