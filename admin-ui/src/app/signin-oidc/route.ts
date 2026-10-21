import * as oidc from "openid-client";
import { cookies } from "next/headers";
import { config, cookieOptions, loginCookie, oidcConfiguration, sessionCookie, sessionFromTokens, sessions } from "@/lib/server";

/** AuthService sends the browser back here with a code; the server exchanges it for tokens and keeps them. */
export async function GET(request: Request) {
  const cookieStore = await cookies();
  const login = cookieStore.get(loginCookie)?.value;
  cookieStore.delete(loginCookie);
  if (!login) {
    return Response.redirect(new URL("login", config().publicUrl).href, 302);
  }

  // AuthService refuses users without access to "auth" before any code is issued.
  if (new URL(request.url).searchParams.get("error") === "access_denied") {
    return Response.redirect(new URL("not-admin", config().publicUrl).href, 302);
  }

  const { verifier, state } = JSON.parse(login) as { verifier: string; state: string };

  // As AuthService knows this page (the registered redirect address), not as the server sees itself.
  const currentUrl = new URL(`signin-oidc${new URL(request.url).search}`, config().publicUrl);
  const tokens = await oidc.authorizationCodeGrant(await oidcConfiguration(), currentUrl, {
    pkceCodeVerifier: verifier,
    expectedState: state,
  });

  // Signing in to "auth" doesn't make someone an admin: only holding auth.Admin does.
  const me = await fetch(new URL("admin/api/me", config().issuer), {
    headers: { Authorization: `Bearer ${tokens.access_token}` },
    cache: "no-store",
  });
  if (!me.ok) {
    return Response.redirect(new URL("not-admin", config().publicUrl).href, 302);
  }

  const id = sessions().create(sessionFromTokens(tokens));
  cookieStore.set(sessionCookie, id, cookieOptions());
  return Response.redirect(config().publicUrl.href, 302);
}
