import "server-only";
import * as oidc from "openid-client";
import { cookies, headers } from "next/headers";
import { redirect } from "next/navigation";
import { AdminApiCaller, cacheUntilFailure, isSameOrigin, type TokenSet } from "./auth-flow";
import { readConfig, type AdminUiConfig } from "./config";
import { SessionStore } from "./sessions";
import { testTokenBody, type TestTokenRequest } from "./test-tokens";

export const sessionCookie = "kale_admin_session";
export const loginCookie = "kale_admin_login";

// One per server process. Kept on globalThis so the development server's hot reload doesn't sign everyone out.
const shared = globalThis as typeof globalThis & {
  kaleConfig?: AdminUiConfig;
  kaleSessions?: SessionStore;
  kaleOidc?: () => Promise<oidc.Configuration>;
  kaleAdminApi?: AdminApiCaller;
};

export function config(): AdminUiConfig {
  shared.kaleConfig ??= readConfig(process.env);
  return shared.kaleConfig;
}

export function sessions(): SessionStore {
  shared.kaleSessions ??= new SessionStore(() => Date.now());
  return shared.kaleSessions;
}

/** AuthService's OpenID Connect metadata, read once (it also checks the issuer matches); retried after a failure. */
export function oidcConfiguration(): Promise<oidc.Configuration> {
  shared.kaleOidc ??= cacheUntilFailure(() => {
    const { issuer, clientId, clientSecret, allowInsecureHttp } = config();
    return oidc.discovery(issuer, clientId, clientSecret, undefined,
      allowInsecureHttp ? { execute: [oidc.allowInsecureRequests] } : undefined);
  });
  return shared.kaleOidc();
}

/** Cookie settings for this UI's own cookies: never readable by scripts, sent only by this site's pages. */
export function cookieOptions(maxAgeSeconds?: number) {
  return {
    httpOnly: true,
    sameSite: "lax" as const,
    secure: config().publicUrl.protocol === "https:",
    path: "/",
    maxAge: maxAgeSeconds,
  };
}

export function tokenSet(tokens: oidc.TokenEndpointResponse & oidc.TokenEndpointResponseHelpers): TokenSet {
  return {
    accessToken: tokens.access_token,
    refreshToken: tokens.refresh_token,
    idToken: tokens.id_token,
    expiresInSeconds: tokens.expiresIn() ?? 0,
  };
}

/** Sends a request to AuthService's admin API with the given access token. */
export function sendToAdminApi(path: string, accessToken: string, init?: RequestInit): Promise<Response> {
  const headers = new Headers(init?.headers);
  headers.set("Authorization", `Bearer ${accessToken}`);
  headers.set("Accept", "application/json");
  return fetch(new URL(`admin/api/${path}`, config().issuer), { ...init, headers, cache: "no-store" });
}

function adminApiCaller(): AdminApiCaller {
  shared.kaleAdminApi ??= new AdminApiCaller(sessions(), () => Date.now(),
    async refreshToken => tokenSet(await oidc.refreshTokenGrant(await oidcConfiguration(), refreshToken)));
  return shared.kaleAdminApi;
}

/**
 * Calls the admin API as the signed-in admin (the token never leaves the server; see AdminApiCaller). Without a
 * session the admin goes to sign in; when AuthService ended it, to a page that says so.
 */
export function adminApi(path: string, init?: RequestInit): Promise<Response> {
  return withAdminToken(accessToken => sendToAdminApi(path, accessToken, init));
}

/** Asks AuthService for a test token (Development/Testing only), proving with the admin's own token who asks. */
export function requestTestToken(request: TestTokenRequest): Promise<Response> {
  return withAdminToken(async accessToken => {
    const tokenEndpoint = (await oidcConfiguration()).serverMetadata().token_endpoint!;
    return fetch(tokenEndpoint, {
      method: "POST",
      headers: { Authorization: `Bearer ${accessToken}`, Accept: "application/json" },
      body: testTokenBody(config().clientId, config().clientSecret, request),
      cache: "no-store",
    });
  });
}

/** Sends a request with the signed-in admin's access token, renewed when needed (see AdminApiCaller). */
async function withAdminToken(send: (accessToken: string) => Promise<Response>): Promise<Response> {
  const sessionId = (await cookies()).get(sessionCookie)?.value;
  const result = await adminApiCaller().call(sessionId, send);
  if (result === "no-session") {
    redirect("/login");
  }

  if (result === "session-ended") {
    // Explains why, and its sign-in link asks for the password (prompt=login).
    redirect("/signed-out?state=session-ended");
  }

  return result;
}

/**
 * Every server action (a change) starts with this. Next.js compares Origin with the Host header itself; this
 * checks it against the UI's configured address, which a proxy in front can't change.
 */
export async function requireSameOrigin(): Promise<void> {
  if (!isSameOrigin(await headers(), config().publicUrl)) {
    throw new Error("Changes are accepted only from the admin UI's own pages.");
  }
}
