import { describe, expect, it, vi } from "vitest";
import type { ApiCall } from "../src/lib/admin-requests";
import {
  accessFromForm, assignRole, createUser, getUser, listUsers, newUserFromForm, setAccess, setUserActive,
  unassignRole, withoutPassword,
} from "../src/lib/users";

function json(body: unknown, status = 200) {
  return new Response(JSON.stringify(body), { status, headers: { "Content-Type": "application/json" } });
}

const noContent = async () => new Response(null, { status: 204 });

describe("reading", () => {
  it("lists users and reads one, escaping the id", async () => {
    const call = vi.fn<ApiCall>(async path => path === "users"
      ? json([{ id: "u1", userName: "deniz", displayName: "Deniz", isActive: true }])
      : json({ id: "u1", userName: "deniz", displayName: "Deniz", isActive: true, applications: [], roles: [], groups: [] }));

    expect(await listUsers(call)).toHaveLength(1);
    expect((await getUser(call, "u/1"))?.userName).toBe("deniz");
    expect(call.mock.calls[1][0]).toBe("users/u%2F1");
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
