import { describe, expect, it } from "vitest";
import * as oidc from "openid-client";
import { RenewalRefused } from "../src/lib/auth-flow";
import { markRefusal } from "../src/lib/token-refusal";

// A real refresh-token grant whose token endpoint answers with `answer` (no network).
function renewWith(answer: () => Promise<Response>) {
  const configuration = new oidc.Configuration(
    { issuer: "https://auth.example/", token_endpoint: "https://auth.example/connect/token" }, "auth", "secret");
  configuration[oidc.customFetch] = answer;
  return markRefusal(() => oidc.refreshTokenGrant(configuration, "refresh"));
}

function json(status: number, body: object) {
  return new Response(JSON.stringify(body), { status, headers: { "Content-Type": "application/json" } });
}

describe("markRefusal", () => {
  it("marks AuthService's OAuth error answer (e.g. invalid_grant: the session ended) as a refusal", async () => {
    await expect(renewWith(async () => json(400, { error: "invalid_grant" }))).rejects.toBeInstanceOf(RenewalRefused);
  });

  it("leaves other failures alone: AuthService unreachable, or a server error", async () => {
    const unreachable = renewWith(async () => { throw new TypeError("fetch failed"); });
    await expect(unreachable).rejects.toThrow("fetch failed");
    await expect(unreachable).rejects.not.toBeInstanceOf(RenewalRefused);

    const failing = renewWith(async () => new Response("Service Unavailable", { status: 503 }));
    await expect(failing).rejects.toThrow();
    await expect(failing).rejects.not.toBeInstanceOf(RenewalRefused);
  });

  it("passes the new tokens through", async () => {
    const tokens = await renewWith(async () => json(200, { access_token: "renewed", token_type: "Bearer", expires_in: 600 }));

    expect(tokens.access_token).toBe("renewed");
  });
});
