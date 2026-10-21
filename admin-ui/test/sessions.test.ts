import { describe, expect, it } from "vitest";
import { SessionStore, needsRefresh, type AdminSession } from "../src/lib/sessions";

const hour = 60 * 60 * 1000;

function session(accessTokenExpiresAt: number): AdminSession {
  return { accessToken: "access", refreshToken: "refresh", idToken: "id", accessTokenExpiresAt };
}

describe("SessionStore", () => {
  it("keeps a session under an unguessable id", () => {
    const store = new SessionStore(() => 0);

    const id = store.create(session(10));

    expect(id.length).toBeGreaterThanOrEqual(43); // 32 random bytes, base64url
    expect(store.get(id)?.accessToken).toBe("access");
    expect(store.get("guess")).toBeUndefined();
  });

  it("forgets a session after 8 hours, like AuthService's own", () => {
    let now = 0;
    const store = new SessionStore(() => now);
    const id = store.create(session(10));

    now = 8 * hour;

    expect(store.get(id)).toBeUndefined();
  });

  it("drops expired sessions nobody asks for again when new ones start", () => {
    let now = 0;
    const store = new SessionStore(() => now);
    store.create(session(10));

    now = 8 * hour;
    store.create(session(10));

    expect(store.size).toBe(1);
  });

  it("can replace a session's tokens and delete it", () => {
    const store = new SessionStore(() => 0);
    const id = store.create(session(10));

    store.update(id, { ...session(20), accessToken: "renewed" });
    expect(store.get(id)?.accessToken).toBe("renewed");

    store.delete(id);
    expect(store.get(id)).toBeUndefined();
  });
});

describe("needsRefresh", () => {
  it("renews a minute before the access token expires", () => {
    expect(needsRefresh(session(120_000), 0)).toBe(false);
    expect(needsRefresh(session(60_000), 0)).toBe(true);
  });
});
