import { describe, expect, it, vi } from "vitest";
import { AdminApiCaller, completeSignIn, isSameOrigin, cacheUntilFailure, type TokenSet } from "../src/lib/auth-flow";
import { SessionStore } from "../src/lib/sessions";

const publicUrl = new URL("https://admin.example/");
const tokens: TokenSet = { accessToken: "access", refreshToken: "refresh", idToken: "id", expiresInSeconds: 600 };
const loginCookie = JSON.stringify({ verifier: "v", state: "s" });

function callbackUrl(query: string) {
  return new URL(`https://internal:8083/signin-oidc?${query}`);
}

describe("completeSignIn", () => {
  it("starts a session for an admin, exchanging the code as the registered address", async () => {
    const sessions = new SessionStore(() => 0);
    const exchangeCode = vi.fn(async () => tokens);

    const outcome = await completeSignIn(callbackUrl("code=c&state=s"), loginCookie, publicUrl, {
      sessions, now: () => 0, exchangeCode, checkAdmin: async () => "admin",
    });

    expect(outcome.kind).toBe("signed-in");
    expect(exchangeCode).toHaveBeenCalledWith(new URL("https://admin.example/signin-oidc?code=c&state=s"), "v", "s");
    expect(sessions.get((outcome as { sessionId: string }).sessionId)?.accessToken).toBe("access");
  });

  it("gives someone without auth.Admin no session, and their id token to end the AuthService session", async () => {
    const sessions = new SessionStore(() => 0);

    const outcome = await completeSignIn(callbackUrl("code=c&state=s"), loginCookie, publicUrl, {
      sessions, now: () => 0, exchangeCode: async () => tokens, checkAdmin: async () => "not-admin",
    });

    expect(outcome).toEqual({ kind: "not-admin", idToken: "id" });
    expect(sessions.size).toBe(0);
  });

  it("treats access_denied (no access to auth) as not an admin, without tokens", async () => {
    const outcome = await completeSignIn(callbackUrl("error=access_denied&state=s"), loginCookie, publicUrl, {
      sessions: new SessionStore(() => 0), now: () => 0, exchangeCode: async () => tokens, checkAdmin: async () => "admin",
    });

    expect(outcome).toEqual({ kind: "not-admin" });
  });

  it.each([
    ["another error", "error=server_error&state=s", loginCookie],
    ["no login in progress", "code=c&state=s", undefined],
    ["a broken login cookie", "code=c&state=s", "{not json"],
  ])("fails cleanly (not a crash) on %s", async (_, query, cookie) => {
    const outcome = await completeSignIn(callbackUrl(query), cookie, publicUrl, {
      sessions: new SessionStore(() => 0), now: () => 0, exchangeCode: async () => tokens, checkAdmin: async () => "admin",
    });

    expect(outcome).toEqual({ kind: "failed" });
  });

  it("fails cleanly when the code exchange is refused (e.g. a state mismatch)", async () => {
    const outcome = await completeSignIn(callbackUrl("code=c&state=other"), loginCookie, publicUrl, {
      sessions: new SessionStore(() => 0), now: () => 0,
      exchangeCode: async () => { throw new Error("unexpected state"); },
      checkAdmin: async () => "admin",
    });

    expect(outcome).toEqual({ kind: "failed" });
  });
});

describe("AdminApiCaller", () => {
  function setUp(expiresInSeconds: number, refresh = vi.fn(async (): Promise<TokenSet> => ({ ...tokens, accessToken: "renewed" }))) {
    const sessions = new SessionStore(() => 0);
    const id = sessions.create({ accessToken: "access", refreshToken: "refresh", idToken: "id", accessTokenExpiresAt: expiresInSeconds * 1000 });
    return { sessions, id, refresh, caller: new AdminApiCaller(sessions, () => 0, refresh) };
  }

  it("calls with the session's access token", async () => {
    const { caller, id } = setUp(600);
    const send = vi.fn(async () => new Response("{}"));

    const result = await caller.call(id, send);

    expect(result).toBeInstanceOf(Response);
    expect(send).toHaveBeenCalledWith("access");
  });

  it("renews a token about to expire first, keeping the refresh token if none comes back", async () => {
    const refresh = vi.fn(async (): Promise<TokenSet> => ({ accessToken: "renewed", expiresInSeconds: 600 }));
    const { caller, id, sessions } = setUp(30, refresh);
    const send = vi.fn(async () => new Response("{}"));

    await caller.call(id, send);

    expect(send).toHaveBeenCalledWith("renewed");
    expect(sessions.get(id)?.refreshToken).toBe("refresh");
    expect(sessions.get(id)?.idToken).toBe("id");
  });

  it("renews once for calls made at the same time", async () => {
    const { caller, id, refresh } = setUp(30);
    const send = async () => new Response("{}");

    await Promise.all([caller.call(id, send), caller.call(id, send)]);

    expect(refresh).toHaveBeenCalledTimes(1);
  });

  it.each([401, 403])("drops the session and asks to sign in again on %i (session stopped, admin rights taken away)", async (status) => {
    const { caller, id, sessions } = setUp(600);

    const result = await caller.call(id, async () => new Response(null, { status }));

    expect(result).toBe("sign-in-again");
    expect(sessions.get(id)).toBeUndefined();
  });

  it("asks to sign in again when the renewal is refused, or there is no session", async () => {
    const { caller, id, sessions } = setUp(30, vi.fn(async (): Promise<TokenSet> => { throw new Error("invalid_grant"); }));

    expect(await caller.call(id, async () => new Response("{}"))).toBe("sign-in-again");
    expect(sessions.get(id)).toBeUndefined();
    expect(await caller.call(undefined, async () => new Response("{}"))).toBe("sign-in-again");
  });
});

describe("isSameOrigin", () => {
  it("accepts a form posted by the admin UI itself and refuses other sites", () => {
    expect(isSameOrigin(new Headers({ origin: "https://admin.example" }), publicUrl)).toBe(true);
    expect(isSameOrigin(new Headers({ origin: "https://evil.example" }), publicUrl)).toBe(false);
    expect(isSameOrigin(new Headers({ "sec-fetch-site": "same-origin" }), publicUrl)).toBe(true);
    expect(isSameOrigin(new Headers({ "sec-fetch-site": "cross-site" }), publicUrl)).toBe(false);
    expect(isSameOrigin(new Headers(), publicUrl)).toBe(false);
  });
});

describe("cacheUntilFailure", () => {
  it("keeps a success but tries again after a failure", async () => {
    let attempts = 0;
    const load = cacheUntilFailure(async () => {
      attempts++;
      if (attempts === 1) {
        throw new Error("AuthService unreachable");
      }

      return "metadata";
    });

    await expect(load()).rejects.toThrow("unreachable");
    expect(await load()).toBe("metadata");
    expect(await load()).toBe("metadata");
    expect(attempts).toBe(2);
  });
});
