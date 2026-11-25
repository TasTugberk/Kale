import * as oidc from "openid-client";
import { cookies } from "next/headers";
import { completeSignIn } from "@/lib/auth-flow";
import { config, cookieOptions, loginCookie, notAdminSignOutUrl, oidcConfiguration, sendToAdminApi, sessionCookie, sessions, tokenSet } from "@/lib/server";

/** AuthService sends the browser back here; what happens next is decided by completeSignIn (lib/auth-flow.ts). */
export async function GET(request: Request) {
  const cookieStore = await cookies();
  const login = cookieStore.get(loginCookie)?.value;
  cookieStore.delete(loginCookie);
  const { publicUrl } = config();

  const outcome = await completeSignIn(new URL(request.url), login, publicUrl, {
    sessions: sessions(),
    now: () => Date.now(),
    exchangeCode: async (currentUrl, verifier, state) => tokenSet(await oidc.authorizationCodeGrant(
      await oidcConfiguration(), currentUrl, { pkceCodeVerifier: verifier, expectedState: state })),
    checkAdmin: async accessToken => {
      const me = await sendToAdminApi("me", accessToken);
      if (me.ok) {
        return "admin";
      }

      // 403 is AuthService's "no auth.Admin"; anything else means it couldn't tell (completeSignIn: try again).
      if (me.status === 403) {
        return "not-admin";
      }

      throw new Error(`The admin API answered ${me.status}.`);
    },
  });

  switch (outcome.kind) {
    case "signed-in":
      cookieStore.set(sessionCookie, outcome.sessionId, cookieOptions());
      return Response.redirect(publicUrl.href, 302);

    case "not-admin":
      if (!outcome.idToken) {
        return Response.redirect(new URL("not-admin", publicUrl).href, 302);
      }

      // The sign-in left an AuthService session for "auth" open: end it, then explain.
      return Response.redirect(await notAdminSignOutUrl(outcome.idToken), 302);

    case "failed":
      return Response.redirect(new URL("sign-in-failed", publicUrl).href, 302);
  }
}
