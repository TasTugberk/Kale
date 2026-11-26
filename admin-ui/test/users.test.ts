import { describe, expect, it, vi } from "vitest";
import type { ApiCall } from "../src/lib/admin-requests";
import {
  accessFromForm, assignRole, countUsers, createUser, getUser, listAllUsers, listUsers, newUserFromForm, passwordFromForm, resetTwoFactor, setAccess,
  setPassword, setUserActive, unassignRole, unlockUser, withoutPassword, updateUser, deleteUser, userNamesFromForm,
} from "../src/lib/users";

function json(body: unknown, status = 200) {
  return new Response(JSON.stringify(body), { status, headers: { "Content-Type": "application/json" } });
}

const noContent = async () => new Response(null, { status: 204 });

const deniz = { id: "u1", userName: "deniz", displayName: "Deniz", isActive: true };

describe("reading", () => {
  it("lists one page of users, searched and sorted on the server", async () => {
    const page = { items: [deniz], total: 41, page: 2, pageSize: 25 };
    const call = vi.fn<ApiCall>(async () => json(page));

    expect(await listUsers(call, { search: "den", page: 2, sort: "displayName", desc: true })).toEqual(page);
    expect(call.mock.calls[0][0]).toBe("users?search=den&page=2&pageSize=25&sort=displayName&desc=true");
  });

  it("reads every user for a picker, page by page at the largest page size", async () => {
    const elif = { ...deniz, id: "u2", userName: "elif" };
    const can = { ...deniz, id: "u3", userName: "can" };
    // 201 users: two pages of 200. Someone was added in between, so elif shows on both pages.
    const call = vi.fn<ApiCall>(async path => path.includes("page=1&")
      ? json({ items: [deniz, elif], total: 201, page: 1, pageSize: 200 })
      : json({ items: [elif, can], total: 201, page: 2, pageSize: 200 }));

    // Each user once (a picker's choices are keyed by id).
    expect((await listAllUsers(call)).map(user => user.userName)).toEqual(["deniz", "elif", "can"]);
    expect(call.mock.calls.map(c => c[0])).toEqual(["users?page=1&pageSize=200", "users?page=2&pageSize=200"]);
  });

  it("counts users, or only the active ones, without reading them", async () => {
    const call = vi.fn<ApiCall>(async () => json({ items: [deniz], total: 1234, page: 1, pageSize: 1 }));

    expect(await countUsers(call)).toBe(1234);
    expect(await countUsers(call, { active: true })).toBe(1234);
    expect(call.mock.calls.map(c => c[0])).toEqual(["users?page=1&pageSize=1", "users?active=true&page=1&pageSize=1"]);
  });

  it("reads one user, escaping the id", async () => {
    const call = vi.fn<ApiCall>(async () => json({ ...deniz, applications: [], roles: [], groups: [] }));

    expect((await getUser(call, "u/1"))?.userName).toBe("deniz");
    expect(call.mock.calls[0][0]).toBe("users/u%2F1");
  });
});

describe("changes", () => {
  it("creates a user", async () => {
    const call = vi.fn<ApiCall>(async () => json({ id: "u1", userName: "deniz", displayName: "Deniz", isActive: true }, 201));

    const outcome = await createUser(call, { userName: "deniz", displayName: "Deniz", password: " secret pass " });

    expect(outcome).toEqual({ ok: true, value: { id: "u1", userName: "deniz", displayName: "Deniz", isActive: true } });
    const [path, init] = call.mock.calls[0];
    expect(path).toBe("users");
    expect(init?.method).toBe("POST");
    expect(JSON.parse(init?.body as string)).toEqual({ userName: "deniz", displayName: "Deniz", password: " secret pass " });
  });

  it("enables or disables a user", async () => {
    const call = vi.fn<ApiCall>(noContent);

    await setUserActive(call, "u1", false);

    expect(call.mock.calls[0][0]).toBe("users/u1/active");
    expect(call.mock.calls[0][1]?.method).toBe("PUT");
    expect(JSON.parse(call.mock.calls[0][1]?.body as string)).toEqual({ isActive: false });
  });

  it("sets access to an application, with a default role or none", async () => {
    const call = vi.fn<ApiCall>(noContent);

    await setAccess(call, "u1", "a1", { isActive: true, defaultRoleId: null });

    expect(call.mock.calls[0][0]).toBe("users/u1/applications/a1");
    expect(call.mock.calls[0][1]?.method).toBe("PUT");
    expect(JSON.parse(call.mock.calls[0][1]?.body as string)).toEqual({ isActive: true, defaultRoleId: null });
  });

  it("assigns and unassigns a role", async () => {
    const call = vi.fn<ApiCall>(noContent);

    await assignRole(call, "u1", "r1");
    await unassignRole(call, "u1", "r1");

    expect(call.mock.calls).toEqual([["users/u1/roles/r1", { method: "PUT" }], ["users/u1/roles/r1", { method: "DELETE" }]]);
  });

  it("sets a new password exactly as given, escaping the id", async () => {
    const call = vi.fn<ApiCall>(noContent);

    expect(await setPassword(call, "u/1", " new pass 12 ")).toEqual({ ok: true, value: undefined });

    expect(call.mock.calls[0][0]).toBe("users/u%2F1/password");
    expect(call.mock.calls[0][1]?.method).toBe("PUT");
    expect(JSON.parse(call.mock.calls[0][1]?.body as string)).toEqual({ password: " new pass 12 " });
  });

  it("resets two-step sign-in with a DELETE, escaping the id", async () => {
    const call = vi.fn<ApiCall>(noContent);

    expect(await resetTwoFactor(call, "u/1")).toEqual({ ok: true, value: undefined });

    expect(call.mock.calls[0][0]).toBe("users/u%2F1/two-factor");
    expect(call.mock.calls[0][1]?.method).toBe("DELETE");
  });

  it("ends a lockout with a DELETE, escaping the id", async () => {
    const call = vi.fn<ApiCall>(noContent);

    expect(await unlockUser(call, "u/1")).toEqual({ ok: true, value: undefined });

    expect(call.mock.calls[0][0]).toBe("users/u%2F1/lockout");
    expect(call.mock.calls[0][1]?.method).toBe("DELETE");
  });

  it("returns why a new password was refused", async () => {
    const refusal = json({ status: 400, detail: "Passwords must be at least 12 characters." }, 400);

    expect(await setPassword(async () => refusal, "u1", "short"))
      .toEqual({ ok: false, refusal: "invalid", detail: "Passwords must be at least 12 characters." });
  });

  it("returns AuthService's reason (e.g. disabling the last admin)", async () => {
    const refusal = json({ status: 400, detail: "That would leave nobody who can administer Kale." }, 400);

    expect(await setUserActive(async () => refusal, "u1", false))
      .toEqual({ ok: false, refusal: "invalid", detail: "That would leave nobody who can administer Kale." });
  });
});

describe("forms", () => {
  it("reads a new user; the password exactly as typed", () => {
    const form = new FormData();
    form.set("userName", " deniz ");
    form.set("displayName", " Deniz Y ");
    form.set("password", "  spaces count  ");

    expect(newUserFromForm(form)).toEqual({ userName: "deniz", displayName: "Deniz Y", password: "  spaces count  " });
  });

  it("reads a new password exactly as typed, and none as empty", () => {
    const form = new FormData();
    expect(passwordFromForm(form)).toBe("");

    form.set("password", "  spaces count  ");
    expect(passwordFromForm(form)).toBe("  spaces count  ");
  });

  it("never sends the password back to the browser after a refusal", () => {
    expect(withoutPassword({ userName: "deniz", displayName: "Deniz", password: "secret" }))
      .toEqual({ userName: "deniz", displayName: "Deniz" });
  });

  it("reads an access row: active or not, and a default role or none", () => {
    const form = new FormData();
    form.set("isActive", "false");
    form.set("defaultRoleId", "");
    expect(accessFromForm(form)).toEqual({ isActive: false, defaultRoleId: null });

    form.set("isActive", "true");
    form.set("defaultRoleId", "r1");
    expect(accessFromForm(form)).toEqual({ isActive: true, defaultRoleId: "r1" });
  });
});

describe("correcting and deleting", () => {
  it("sends the corrected names and deletes a user, escaping the id", async () => {
    const call = vi.fn<ApiCall>(noContent);

    expect(await updateUser(call, "u/1", { userName: "deniz.yilmaz", displayName: "Deniz Yılmaz" })).toEqual({ ok: true, value: undefined });
    await deleteUser(call, "u/1");

    expect(call.mock.calls[0][0]).toBe("users/u%2F1");
    expect(call.mock.calls[0][1]?.method).toBe("PUT");
    expect(JSON.parse(call.mock.calls[0][1]?.body as string)).toEqual({ userName: "deniz.yilmaz", displayName: "Deniz Yılmaz" });
    expect(call.mock.calls[1]).toEqual(["users/u%2F1", { method: "DELETE" }]);
  });

  it("passes on AuthService's reason when it refuses", async () => {
    const reason = "You can't delete your own account; another administrator can.";
    const call = vi.fn<ApiCall>(async () => json({ detail: reason }, 400));

    expect(await deleteUser(call, "u1")).toEqual({ ok: false, refusal: "invalid", detail: reason });
  });

  it("reads the names from the form, trimmed", () => {
    const form = new FormData();
    form.set("userName", " deniz ");
    form.set("displayName", " Deniz ");

    expect(userNamesFromForm(form)).toEqual({ userName: "deniz", displayName: "Deniz" });
  });
});
