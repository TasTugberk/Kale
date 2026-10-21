import "server-only";
import * as oidc from "openid-client";
import { cookies } from "next/headers";
import { redirect } from "next/navigation";
import { readConfig, type AdminUiConfig } from "./config";
import { SessionStore, needsRefresh, type AdminSession } from "./sessions";

export const sessionCookie = "kale_admin_session";
export const loginCookie = "kale_admin_login";

// One per server process. Kept on globalThis so the development server's hot reload doesn't sign everyone out.
const shared = globalThis as typeof globalThis & {
  kaleConfig?: AdminUiConfig;
  kaleSessions?: SessionStore;
  kaleOidc?: Promise<oidc.Configuration>;
};

export function config(): AdminUiConfig {
  shared.kaleConfig ??= readConfig(process.env);
  return shared.kaleConfig;
}

export function sessions(): SessionStore {
  shared.kaleSessions ??= new SessionStore(() => Date.now());
  return shared.kaleSessions;
}

/** AuthService's OpenID Connect metadata, read once (it also checks the issuer matches). */
export function oidcConfiguration(): Promise<oidc.Configuration> {
  const { issuer, clientId, clientSecret, allowInsecureHttp } = config();
  shared.kaleOidc ??= oidc.discovery(issuer, clientId, clientSecret, undefined,
    allowInsecureHttp ? { execute: [oidc.allowInsecureRequests] } : undefined);
  return shared.kaleOidc;
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

export function sessionFromTokens(tokens: oidc.TokenEndpointResponse & oidc.TokenEndpointResponseHelpers): AdminSession {
  return {
    accessToken: tokens.access_token,
    refreshToken: tokens.refresh_token,
    idToken: tokens.id_token,
    accessTokenExpiresAt: Date.now() + (tokens.expiresIn() ?? 0) * 1000,
  };
}

/**
 * Calls AuthService's admin API as the signed-in admin. The access token never leaves the server; it's renewed
 * shortly before it expires. Without a usable session the admin goes to sign in.
 */
export async function adminApi(path: string, init?: RequestInit): Promise<Response> {
  const id = (await cookies()).get(sessionCookie)?.value;
  let session = id ? sessions().get(id) : undefined;
  if (!id || !session) {
    redirect("/login");
  }

  if (needsRefresh(session, Date.now())) {
    session = await refreshed(id, session);
  }

  const response = await fetch(new URL(`admin/api/${path}`, config().issuer), {
    ...init,
    headers: { ...init?.headers, Authorization: `Bearer ${session.accessToken}`, Accept: "application/json" },
    cache: "no-store",
  });

  // The session ended (stopped by an admin, user disabled, ...): sign in again.
  if (response.status === 401) {
    sessions().delete(id);
    redirect("/login");
  }

  return response;
}

async function refreshed(id: string, session: AdminSession): Promise<AdminSession> {
  if (!session.refreshToken) {
    sessions().delete(id);
    redirect("/login");
  }

  try {
    const tokens = await oidc.refreshTokenGrant(await oidcConfiguration(), session.refreshToken);
    const renewed = { ...sessionFromTokens(tokens), idToken: tokens.id_token ?? session.idToken };
    sessions().update(id, renewed);
    return renewed;
  } catch {
    // AuthService refuses the refresh once the session is no longer active.
    sessions().delete(id);
    redirect("/login");
  }
}
