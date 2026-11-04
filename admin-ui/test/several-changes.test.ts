import { describe, expect, it, vi } from "vitest";
import { changeEach, listFromForm, type ApiCall } from "../src/lib/admin-requests";
import { addGroupRoles, addMembers } from "../src/lib/groups";
import { assignRoles } from "../src/lib/users";

const noContent = async () => new Response(null, { status: 204 });

function form(entries: [string, string][]) {
  const data = new FormData();
  for (const [name, value] of entries) {
    data.append(name, value);
  }

  return data;
}

describe("a multi-select's values", () => {
  it("are every value sent under the name, trimmed, without blanks or duplicates", () => {
    const data = form([["roleId", " r1 "], ["roleId", ""], ["roleId", "r2"], ["roleId", "r1"], ["other", "x"]]);

    expect(listFromForm(data, "roleId")).toEqual(["r1", "r2"]);
    expect(listFromForm(data, "missing")).toEqual([]);
  });
});

describe("several changes, one after another", () => {
  it("makes each change in order", async () => {
    const done: string[] = [];

    expect(await changeEach(["a", "b"], async id => {
      done.push(id);
      return { ok: true, value: undefined };
    })).toEqual({ ok: true, value: undefined });
    expect(done).toEqual(["a", "b"]);
  });

  // The ones before stay made (each is its own change in AuthService); the page shows them and the refusal.
  it("stops at the first refusal and reports it", async () => {
    const done: string[] = [];

    const outcome = await changeEach(["a", "b", "c"], async id => {
      done.push(id);
      return id === "b" ? { ok: false, refusal: "invalid", detail: "No." } : { ok: true, value: undefined };
    });

    expect(outcome).toEqual({ ok: false, refusal: "invalid", detail: "No." });
    expect(done).toEqual(["a", "b"]);
  });

  it("does nothing for nothing chosen", async () => {
    const change = vi.fn();

    expect(await changeEach([], change)).toEqual({ ok: true, value: undefined });
    expect(change).not.toHaveBeenCalled();
  });
});

describe("several at once", () => {
  it("assigns several roles to a user", async () => {
    const call = vi.fn<ApiCall>(noContent);

    expect(await assignRoles(call, "u/1", ["r1", "r2"])).toEqual({ ok: true, value: undefined });
    expect(call.mock.calls.map(c => [c[0], c[1]?.method])).toEqual([["users/u%2F1/roles/r1", "PUT"], ["users/u%2F1/roles/r2", "PUT"]]);
  });

  it("adds several members and several roles to a group", async () => {
    const call = vi.fn<ApiCall>(noContent);

    await addMembers(call, "g1", ["u1", "u2"]);
    await addGroupRoles(call, "g1", ["r1"]);

    expect(call.mock.calls.map(c => [c[0], c[1]?.method]))
      .toEqual([["groups/g1/members/u1", "PUT"], ["groups/g1/members/u2", "PUT"], ["groups/g1/roles/r1", "PUT"]]);
  });
});
