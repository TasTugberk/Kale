import { describe, expect, it, vi } from "vitest";
import type { ApiCall } from "../src/lib/admin-requests";
import { listOpenSessions, stopSession } from "../src/lib/open-sessions";

const session = {
  id: "s1", userId: "u1", userName: "deniz", applicationKey: "sample", roleName: "Reader",
  createdAt: "2026-10-22T08:00:00+00:00", expiresAt: "2026-10-22T16:00:00+00:00",
};

const firstPage = { search: "", page: 1, sort: undefined, desc: false };

describe("listOpenSessions", () => {
  it("lists one page of open sessions", async () => {
    const page = { items: [session], total: 1, page: 1, pageSize: 25 };
    const call = vi.fn<ApiCall>(async () => new Response(JSON.stringify(page)));

    expect(await listOpenSessions(call, {}, firstPage)).toEqual(page);
    expect(call.mock.calls[0][0]).toBe("sessions?page=1&pageSize=25");
  });

  it("filters by application and user, and searches on the server", async () => {
    const call = vi.fn<ApiCall>(async () => new Response(JSON.stringify({ items: [], total: 0, page: 1, pageSize: 25 })));

    await listOpenSessions(call, { applicationId: "a1", userId: "u&1" }, { ...firstPage, search: "bill" });

    expect(call.mock.calls[0][0]).toBe("sessions?applicationId=a1&userId=u%261&search=bill&page=1&pageSize=25");
  });
});

describe("stopSession", () => {
  it("stops it, and says when the session no longer exists (a 404; an ended one still answers 204)", async () => {
    const call = vi.fn<ApiCall>(async () => new Response(null, { status: 204 }));

    expect(await stopSession(call, "s1")).toEqual({ ok: true, value: undefined });
    expect(call.mock.calls[0]).toEqual(["sessions/s1", { method: "DELETE" }]);
    expect(await stopSession(async () => new Response(null, { status: 404 }), "s1")).toEqual({ ok: false, refusal: "not-found" });
  });
});
