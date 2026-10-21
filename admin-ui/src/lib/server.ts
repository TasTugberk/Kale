import "server-only";
import * as oidc from "openid-client";
import { cookies } from "next/headers";
import { redirect } from "next/navigation";
import { AdminApiCaller, cacheUntilFailure, type TokenSet } from "./auth-flow";
import { readConfig, type AdminUiConfig } from "./config";
import { SessionStore } from "./sessions";

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
 * Calls the admin API as the signed-in admin (the token never leaves the server; see AdminApiCaller). When the
 * session can't be used any more, the admin goes to sign in again.
 */
export async function adminApi(path: string, init?: RequestInit): Promise<Response> {
  const sessionId = (await cookies()).get(sessionCookie)?.value;
  const result = await adminApiCaller().call(sessionId, accessToken => sendToAdminApi(path, accessToken, init));
  if (result === "sign-in-again") {
    redirect("/login");
  }

  return result;
}
