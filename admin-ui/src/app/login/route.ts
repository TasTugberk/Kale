import * as oidc from "openid-client";
import { cookies } from "next/headers";
import { config, cookieOptions, loginCookie, oidcConfiguration } from "@/lib/server";

/**
 * Starts signing in: authorization code with PKCE (S256), as every Kale client does. ?prompt=login makes
 * AuthService ask for credentials even when the browser is still signed in there ("another account").
 */
export async function GET(request: Request) {
  const verifier = oidc.randomPKCECodeVerifier();
  const state = oidc.randomState();
  const parameters: Record<string, string> = {
    redirect_uri: new URL("signin-oidc", config().publicUrl).href,
    scope: "openid offline_access",
    code_challenge: await oidc.calculatePKCECodeChallenge(verifier),
    code_challenge_method: "S256",
    state,
  };
  if (new URL(request.url).searchParams.get("prompt") === "login") {
    parameters.prompt = "login";
  }

  const url = oidc.buildAuthorizationUrl(await oidcConfiguration(), parameters);

  // Ten minutes to finish signing in; the callback needs both to check the answer belongs to this attempt.
  (await cookies()).set(loginCookie, JSON.stringify({ verifier, state }), cookieOptions(600));
  return Response.redirect(url.href, 302);
}
