import { describe, expect, it, vi } from "vitest";
import type { ApiCall } from "../src/lib/admin-requests";
import {
  addRoleOperations, findRole, grantableOperations, obsoleteDroppedWith, obsoleteGrants, operationsAfterAdding,
  operationsAfterRemoving, removeRoleOperation, type Operation,
} from "../src/lib/roles";

function json(body: unknown, status = 200) {
  return new Response(JSON.stringify(body), { status, headers: { "Content-Type": "application/json" } });
}

const operations: Operation[] = [
  { id: "o1", name: "billing.Read", isObsolete: false },
  { id: "o2", name: "billing.Write", isObsolete: false },
  { id: "o3", name: "billing.Approve", isObsolete: false },
  { id: "o4", name: "billing.Old", isObsolete: true },
];

describe("the operations a role can still be given", () => {
  it("are the current ones it doesn't hold yet", () => {
    const role = { id: "r1", name: "Clerk", description: null, operations: ["billing.Read", "billing.Old"] };

    expect(grantableOperations(role, operations).map(o => o.name)).toEqual(["billing.Write", "billing.Approve"]);
  });
});

describe("adding and removing operations", () => {
  it("adds to what the role holds, without duplicates", () => {
    expect(operationsAfterAdding(["billing.Read"], ["billing.Write", "billing.Read"], operations))
      .toEqual(["billing.Read", "billing.Write"]);
  });

  // AuthService refuses to save an obsolete operation, so keeping one would make every change fail.
  it("drops obsolete grants, which can't be saved", () => {
    expect(operationsAfterAdding(["billing.Read", "billing.Old"], ["billing.Write"], operations))
      .toEqual(["billing.Read", "billing.Write"]);
    expect(operationsAfterRemoving(["billing.Read", "billing.Write", "billing.Old"], "billing.Write", operations))
      .toEqual(["billing.Read"]);
  });

  it("removes one operation, including an obsolete one", () => {
    expect(operationsAfterRemoving(["billing.Read", "billing.Write"], "billing.Read", operations)).toEqual(["billing.Write"]);
    expect(operationsAfterRemoving(["billing.Read", "billing.Old"], "billing.Old", operations)).toEqual(["billing.Read"]);
  });

  // The confirmation for removing one operation names them, so they don't disappear unannounced.
  it("tells which obsolete grants go along with a removed operation", () => {
    const granted = ["billing.Read", "billing.Write", "billing.Old"];
    const obsolete = obsoleteGrants(granted, operations);
    const lost = granted.filter(name => !operationsAfterRemoving(granted, "billing.Write", operations).includes(name));

    expect(obsoleteDroppedWith("billing.Write", obsolete)).toEqual(lost.filter(name => name !== "billing.Write"));
    expect(obsoleteDroppedWith("billing.Write", obsolete)).toEqual(["billing.Old"]);
    // Removing the obsolete grant itself takes nothing else.
    expect(obsoleteDroppedWith("billing.Old", obsolete)).toEqual([]);
  });
});

describe("one role of an application", () => {
  it("is found in the application's roles (there's no endpoint for one role)", async () => {
    const call = vi.fn<ApiCall>(async () => json([
      { id: "r1", name: "Clerk", description: null, operations: [] },
      { id: "r2", name: "Boss", description: null, operations: [] },
    ]));

    expect((await findRole(call, "a/1", "r2"))?.name).toBe("Boss");
    expect(await findRole(call, "a/1", "r9")).toBeUndefined();
    expect(call.mock.calls[0][0]).toBe("applications/a%2F1/roles");
  });
});

describe("changing a role's operations", () => {
  // What the role holds right now, read on the server: the page may be older than another admin's change.
  function api(roleOperations: string[]) {
    return vi.fn<ApiCall>(async (path, init) => {
      if (init?.method === "PUT") {
        return new Response(null, { status: 204 });
      }

      return path.endsWith("/operations")
        ? json(operations)
        : json([{ id: "r1", name: "Clerk", description: null, operations: roleOperations }]);
    });
  }

  function sent(call: ReturnType<typeof api>) {
    const put = call.mock.calls.find(c => c[1]?.method === "PUT")!;
    return { path: put[0], operations: (JSON.parse(put[1]?.body as string) as { operations: string[] }).operations };
  }

  it("adds the chosen operations to what the role holds now", async () => {
    const call = api(["billing.Read", "billing.Approve"]);

    expect(await addRoleOperations(call, "app", "r1", ["billing.Write"])).toEqual({ ok: true, value: undefined });
    expect(sent(call)).toEqual({ path: "roles/r1/operations", operations: ["billing.Read", "billing.Approve", "billing.Write"] });
  });

  it("removes one operation from what the role holds now", async () => {
    const call = api(["billing.Read", "billing.Approve"]);

    expect(await removeRoleOperation(call, "app", "r1", "billing.Read")).toEqual({ ok: true, value: undefined });
    expect(sent(call)).toEqual({ path: "roles/r1/operations", operations: ["billing.Approve"] });
  });

  it("says the role is gone when it was deleted meanwhile, and changes nothing", async () => {
    const call = api([]);
    const gone = vi.fn<ApiCall>(async (path, init) => path.endsWith("/roles") ? json([]) : call(path, init));

    expect(await addRoleOperations(gone, "app", "r1", ["billing.Write"])).toEqual({ ok: false, refusal: "not-found" });
    expect(await removeRoleOperation(gone, "app", "r1", "billing.Read")).toEqual({ ok: false, refusal: "not-found" });
    expect(gone.mock.calls.some(c => c[1]?.method === "PUT")).toBe(false);
  });
});
