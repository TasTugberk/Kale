import { describe, expect, it } from "vitest";
import { readTestTokenAnswer, testTokenBody, testTokenRequestFromForm } from "../src/lib/test-tokens";

describe("testTokenBody", () => {
  it("asks connect/token for a test token as the admin UI's own client", () => {
    const body = testTokenBody("auth", "ui-secret", { applicationKey: "billing", userId: "u1", roleId: "r1" });

    expect(Object.fromEntries(body)).toEqual({
      grant_type: "urn:kale:params:oauth:grant-type:test-token",
      client_id: "auth",
      client_secret: "ui-secret",
      application: "billing",
      user_id: "u1",
      role_id: "r1",
    });
  });

  it("leaves the role out when none is chosen (the user's default or only role)", () => {
    const body = testTokenBody("auth", "ui-secret", { applicationKey: "billing", userId: "u1" });

    expect(body.has("role_id")).toBe(false);
  });
});

describe("readTestTokenAnswer", () => {
  it("reads the access token and its lifetime", async () => {
    const answer = new Response(JSON.stringify({ access_token: "token", token_type: "Bearer", expires_in: 600 }));

    expect(await readTestTokenAnswer(answer)).toEqual({ ok: true, value: { accessToken: "token", expiresInSeconds: 600 } });
  });

  it("returns AuthService's reason when it refuses", async () => {
    const answer = new Response(JSON.stringify({ error: "invalid_grant", error_description: "No active access to this application." }), { status: 400 });

    expect(await readTestTokenAnswer(answer)).toEqual({ ok: false, refusal: "invalid", detail: "No active access to this application." });
  });

  it("throws on anything else, for the error page", async () => {
    await expect(readTestTokenAnswer(new Response("oops", { status: 500 }))).rejects.toThrow("500");
  });
});

describe("testTokenRequestFromForm", () => {
  it("reads the application, the user and the optional role", () => {
    const form = new FormData();
    form.set("applicationKey", "billing");
    form.set("userId", "u1");
    form.set("roleId", "");

    expect(testTokenRequestFromForm(form)).toEqual({ applicationKey: "billing", userId: "u1", roleId: undefined });
  });
});
