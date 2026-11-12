import * as oidc from "openid-client";
import { RenewalRefused } from "./auth-flow";

/**
 * Runs a token renewal and tells a refusal from a failure: when AuthService answered with an OAuth error (e.g.
 * invalid_grant once the session has ended), it throws RenewalRefused. Anything else (AuthService unreachable,
 * a server error) is thrown as it is. Kept apart from auth-flow.ts, which doesn't depend on openid-client.
 */
export async function markRefusal<T>(renewal: () => Promise<T>): Promise<T> {
  try {
    return await renewal();
  } catch (error) {
    // openid-client throws ResponseBodyError only for a 4xx answer with an OAuth error in its body.
    if (error instanceof oidc.ResponseBodyError) {
      throw new RenewalRefused(error.error, { cause: error });
    }

    throw error;
  }
}
