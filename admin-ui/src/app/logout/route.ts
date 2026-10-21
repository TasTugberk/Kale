import * as oidc from "openid-client";
import { cookies } from "next/headers";
import { config, oidcConfiguration, sessionCookie, sessions } from "@/lib/server";

/**
 * Signs out here and at AuthService, which ends this session and clears its own sign-in cookie. A form post
 * (not a link), so other sites can't sign an admin out.
 */
export async function POST() {
  const cookieStore = await cookies();
  const id = cookieStore.get(sessionCookie)?.value;
  const idToken = id ? sessions().get(id)?.idToken : undefined;
  if (id) {
    sessions().delete(id);
  }

  cookieStore.delete(sessionCookie);
  const signedOut = new URL("signed-out", config().publicUrl).href;
  if (!idToken) {
    return Response.redirect(signedOut, 303);
  }

  const endSession = oidc.buildEndSessionUrl(await oidcConfiguration(), {
    id_token_hint: idToken,
    post_logout_redirect_uri: signedOut,
  });
  return Response.redirect(endSession.href, 303);
}
