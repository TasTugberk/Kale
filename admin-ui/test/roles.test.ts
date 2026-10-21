import { describe, expect, it, vi } from "vitest";
import type { ApiCall } from "../src/lib/admin-requests";
import {
  createRole, deleteRole, listOperations, listRoles, obsoleteGrants, operationsFromForm, renameRole, roleFromForm,
  setRoleOperations,
} from "../src/lib/roles";

function json(body: unknown, status = 200) {
  return new Response(JSON.stringify(body), { status, headers: { "Content-Type": "application/json" } });
}

const noContent = () => new Response(null, { status: 204 });

describe("reading", () => {
  it("lists an application's operations and roles", async () => {
    const call = vi.fn<ApiCall>(async path => path.endsWith("/operations")
      ? json([{ id: "o1", name: "billing.Read", isObsolete: false }])
      : json([{ id: "r1", name: "Reader", description: null, operations: ["billing.Read"] }]));

    expect(await listOperations(call, "a/1")).toEqual([{ id: "o1", name: "billing.Read", isObsolete: false }]);
    expect(await listRoles(call, "a/1")).toEqual([{ id: "r1", name: "Reader", description: null, operations: ["billing.Read"] }]);
    expect(call.mock.calls.map(c => c[0])).toEqual(["applications/a%2F1/operations", "applications/a%2F1/roles"]);
  });
});

describe("changes", () => {
  it("creates a role in the application", async () => {
    const call = vi.fn<ApiCall>(async () => json({ id: "r1", name: "Reader", description: null }, 201));

    expect(await createRole(call, "app", { name: "Reader", description: null })).toEqual({ ok: true, value: undefined });
    const [path, init] = call.mock.calls[0];
    expect(path).toBe("applications/app/roles");
    expect(init?.method).toBe("POST");
    expect(JSON.parse(init?.body as string)).toEqual({ name: "Reader", description: null });
  });

  it("renames a role", async () => {
    const call = vi.fn<ApiCall>(async () => noContent());

    expect(await renameRole(call, "r1", { name: "Readers", description: "Can read" })).toEqual({ ok: true, value: undefined });
    const [path, init] = call.mock.calls[0];
    expect(path).toBe("roles/r1");
    expect(init?.method).toBe("PUT");
    expect(JSON.parse(init?.body as string)).toEqual({ name: "Readers", description: "Can read" });
  });

  it("replaces a role's operations", async () => {
    const call = vi.fn<ApiCall>(async () => noContent());

    await setRoleOperations(call, "r1", ["billing.Read", "billing.Write"]);

    const [path, init] = call.mock.calls[0];
    expect(path).toBe("roles/r1/operations");
    expect(init?.method).toBe("PUT");
    expect(JSON.parse(init?.body as string)).toEqual({ operations: ["billing.Read", "billing.Write"] });
  });

  it("deletes a role, and says when it was already gone", async () => {
    const call = vi.fn<ApiCall>(async () => noContent());

    expect(await deleteRole(call, "r1")).toEqual({ ok: true, value: undefined });
    expect(call.mock.calls[0]).toEqual(["roles/r1", { method: "DELETE" }]);
    expect(await deleteRole(async () => new Response(null, { status: 404 }), "r1")).toEqual({ ok: false, refusal: "not-found" });
  });

  it("returns AuthService's reason (e.g. the last admin role)", async () => {
    const refusal = json({ status: 400, detail: "Nobody would hold auth.Admin." }, 400);

    expect(await deleteRole(async () => refusal, "r1")).toEqual({ ok: false, refusal: "invalid", detail: "Nobody would hold auth.Admin." });
  });
});

describe("forms", () => {
  it("reads a role's name and description; an empty description is none", () => {
    const form = new FormData();
    form.set("name", " Reader ");
    form.set("description", "  ");

    expect(roleFromForm(form)).toEqual({ name: "Reader", description: null });
  });

  it("reads the ticked operations", () => {
    const form = new FormData();
    form.append("operation", "billing.Read");
    form.append("operation", "billing.Write");

    expect(operationsFromForm(form)).toEqual(["billing.Read", "billing.Write"]);
    expect(operationsFromForm(new FormData())).toEqual([]);
  });
});

describe("obsoleteGrants", () => {
  it("lists the operations a role holds that the application no longer registers", () => {
    const operations = [
      { id: "1", name: "billing.Read", isObsolete: false },
      { id: "2", name: "billing.Old", isObsolete: true },
    ];

    expect(obsoleteGrants(["billing.Old", "billing.Read"], operations)).toEqual(["billing.Old"]);
  });
});
