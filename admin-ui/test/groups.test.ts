import { describe, expect, it, vi } from "vitest";
import type { ApiCall } from "../src/lib/admin-requests";
import {
  addGroupRole, addMember, createGroup, deleteGroup, getGroup, groupFromForm, listGroups, removeGroupRole, removeMember,
  renameGroup,
} from "../src/lib/groups";

function json(body: unknown, status = 200) {
  return new Response(JSON.stringify(body), { status, headers: { "Content-Type": "application/json" } });
}

const noContent = async () => new Response(null, { status: 204 });

describe("reading", () => {
  it("lists groups and reads one, escaping the id", async () => {
    const call = vi.fn<ApiCall>(async path => path === "groups"
      ? json([{ id: "g1", name: "Finance", description: null }])
      : json({ id: "g1", name: "Finance", description: null, members: [], roles: [] }));

    expect(await listGroups(call)).toEqual([{ id: "g1", name: "Finance", description: null }]);
    expect((await getGroup(call, "g/1"))?.name).toBe("Finance");
    expect(call.mock.calls[1][0]).toBe("groups/g%2F1");
  });

  it("gives undefined for an unknown group", async () => {
    expect(await getGroup(async () => new Response(null, { status: 404 }), "g1")).toBeUndefined();
  });
});

describe("changes", () => {
  it("creates a group and returns it, to link to it", async () => {
    const call = vi.fn<ApiCall>(async () => json({ id: "g1", name: "Finance", description: "Money" }, 201));

    expect(await createGroup(call, { name: "Finance", description: "Money" }))
      .toEqual({ ok: true, value: { id: "g1", name: "Finance", description: "Money" } });
    expect(call.mock.calls[0][0]).toBe("groups");
    expect(call.mock.calls[0][1]?.method).toBe("POST");
    expect(JSON.parse(call.mock.calls[0][1]?.body as string)).toEqual({ name: "Finance", description: "Money" });
  });

  it("renames and deletes a group", async () => {
    const call = vi.fn<ApiCall>(noContent);

    await renameGroup(call, "g1", { name: "Finans", description: null });
    await deleteGroup(call, "g1");

    expect(call.mock.calls[0][0]).toBe("groups/g1");
    expect(call.mock.calls[0][1]?.method).toBe("PUT");
    expect(JSON.parse(call.mock.calls[0][1]?.body as string)).toEqual({ name: "Finans", description: null });
    expect(call.mock.calls[1]).toEqual(["groups/g1", { method: "DELETE" }]);
  });

  it("adds and removes members and roles", async () => {
    const call = vi.fn<ApiCall>(noContent);

    await addMember(call, "g1", "u1");
    await removeMember(call, "g1", "u1");
    await addGroupRole(call, "g1", "r1");
    await removeGroupRole(call, "g1", "r1");

    expect(call.mock.calls).toEqual([
      ["groups/g1/members/u1", { method: "PUT" }],
      ["groups/g1/members/u1", { method: "DELETE" }],
      ["groups/g1/roles/r1", { method: "PUT" }],
      ["groups/g1/roles/r1", { method: "DELETE" }],
    ]);
  });

  it("returns AuthService's reason (e.g. the last admin's group)", async () => {
    const refusal = json({ status: 400, detail: "That would leave nobody who can administer Kale." }, 400);

    expect(await removeMember(async () => refusal, "g1", "u1"))
      .toEqual({ ok: false, refusal: "invalid", detail: "That would leave nobody who can administer Kale." });
  });
});

describe("groupFromForm", () => {
  it("reads the name and description; an empty description is none", () => {
    const form = new FormData();
    form.set("name", " Finance ");
    form.set("description", " ");

    expect(groupFromForm(form)).toEqual({ name: "Finance", description: null });
  });
});
