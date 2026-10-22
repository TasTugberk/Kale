import { text, type Outcome } from "./admin-requests";

/**
 * Test tokens (Development/Testing only): the admin UI asks AuthService's connect/token for a user's access token
 * to an application, so developers can try their API without building a sign-in first. The request carries the
 * admin UI's own client credentials, and the admin's access token as the bearer (see server.ts).
 */

export const testTokenGrantType = "urn:kale:params:oauth:grant-type:test-token";

export interface TestTokenRequest {
  applicationKey: string;
  userId: string;
  /** Without one, the user's default or only role is used. */
  roleId?: string;
}

export interface TestToken {
  accessToken: string;
  expiresInSeconds: number;
}

export function testTokenBody(clientId: string, clientSecret: string, request: TestTokenRequest): URLSearchParams {
  const body = new URLSearchParams({
    grant_type: testTokenGrantType,
    client_id: clientId,
    client_secret: clientSecret,
    application: request.applicationKey,
    user_id: request.userId,
  });
  if (request.roleId) {
    body.set("role_id", request.roleId);
  }

  return body;
}

/** A refusal (400) comes back to the form with AuthService's reason; anything else is an error for the error page. */
export async function readTestTokenAnswer(response: Response): Promise<Outcome<TestToken>> {
  if (response.ok) {
    const body = (await response.json()) as { access_token: string; expires_in: number };
    return { ok: true, value: { accessToken: body.access_token, expiresInSeconds: body.expires_in } };
  }

  if (response.status === 400) {
    const body = (await response.json()) as { error_description?: string };
    return { ok: false, refusal: "invalid", detail: body.error_description };
  }

  throw new Error(`AuthService's token endpoint answered ${response.status}.`);
}

export function testTokenRequestFromForm(form: FormData): TestTokenRequest {
  const roleId = text(form, "roleId");
  return { applicationKey: text(form, "applicationKey"), userId: text(form, "userId"), roleId: roleId === "" ? undefined : roleId };
}
