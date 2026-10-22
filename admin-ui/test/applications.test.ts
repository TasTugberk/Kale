import { describe, expect, it, vi } from "vitest";
import {
  applicationChangesFromForm, canRotateSecret, changeApplication, createApplication, getApplication, linesOf,
  listApplications, newApplicationFromForm, rotateSecret, setApplicationActive,
} from "../src/lib/applications";
import type { ApiCall } from "../src/lib/admin-requests";

function json(body: unknown, status = 200) {
  return new Response(JSON.stringify(body), { status, headers: { "Content-Type": "application/json" } });
}

function problem(detail: string) {
  return json({ title: "Bad Request", status: 400, detail }, 400);
}

describe("listApplications", () => {
  it("reads the list", async () => {
    const call = vi.fn<ApiCall>(async () => json([{ id: "1", key: "billing", name: "Billing", isActive: true }]));

    const applications = await listApplications(call);

    expect(applications).toEqual([{ id: "1", key: "billing", name: "Billing", isActive: true }]);
    expect(call).toHaveBeenCalledWith("applications", undefined);
  });

  it("throws when AuthService fails, so the page shows its error", async () => {
    await expect(listApplications(async () => new Response(null, { status: 503 }))).rejects.toThrow("503");
  });
});

describe("createApplication", () => {
  const request = { key: "billing", name: "Billing", redirectUris: ["https://billing.example/signin-oidc"], postLogoutRedirectUris: [] };

  it("posts the new application and returns its secret", async () => {
    const call = vi.fn<ApiCall>(async () => json({ id: "1", key: "billing", name: "Billing", isActive: true, clientSecret: "s3cret" }, 201));

    const outcome = await createApplication(call, request);

    expect(outcome).toEqual({ ok: true, value: { key: "billing", clientSecret: "s3cret" } });
    const [path, init] = call.mock.calls[0];
    expect(path).toBe("applications");
    expect(init?.method).toBe("POST");
    expect(new Headers(init?.headers).get("Content-Type")).toBe("application/json");
    expect(JSON.parse(init?.body as string)).toEqual(request);
  });

  it("returns AuthService's reason when it refuses the input", async () => {
    const outcome = await createApplication(async () => problem("The key 'billing' is already in use."), request);

    expect(outcome).toEqual({ ok: false, refusal: "invalid", detail: "The key 'billing' is already in use." });
  });

  it("still says the input was refused when the 400 has no reason (e.g. a body that didn't bind)", async () => {
    const outcome = await createApplication(async () => new Response(null, { status: 400 }), request);

    expect(outcome).toEqual({ ok: false, refusal: "invalid", detail: undefined });
  });
});

describe("rotateSecret", () => {
  it("returns the new secret", async () => {
    const call = vi.fn<ApiCall>(async () => json({ clientSecret: "new" }));

    expect(await rotateSecret(call, "abc")).toEqual({ ok: true, value: "new" });
    expect(call.mock.calls[0]).toEqual(["applications/abc/secret", { method: "POST" }]);
  });

  it("says when the application no longer exists", async () => {
    expect(await rotateSecret(async () => new Response(null, { status: 404 }), "abc")).toEqual({ ok: false, refusal: "not-found" });
  });

  it("escapes the id, so it can't reach another path", async () => {
    const call = vi.fn<ApiCall>(async () => json({ clientSecret: "new" }));

    await rotateSecret(call, "../users");

    expect(call.mock.calls[0][0]).toBe("applications/..%2Fusers/secret");
  });
});

describe("setApplicationActive", () => {
  it("puts the new state", async () => {
    const call = vi.fn<ApiCall>(async () => new Response(null, { status: 204 }));

    expect(await setApplicationActive(call, "abc", false)).toEqual({ ok: true, value: undefined });
    const [path, init] = call.mock.calls[0];
    expect(path).toBe("applications/abc/active");
    expect(init?.method).toBe("PUT");
    expect(JSON.parse(init?.body as string)).toEqual({ isActive: false });
  });

  it("returns the reason when AuthService refuses (e.g. deactivating auth)", async () => {
    const outcome = await setApplicationActive(async () => problem("The application 'auth' can't be deactivated."), "abc", false);

    expect(outcome).toEqual({ ok: false, refusal: "invalid", detail: "The application 'auth' can't be deactivated." });
  });

  it("throws on anything unexpected", async () => {
    await expect(setApplicationActive(async () => new Response(null, { status: 500 }), "abc", true)).rejects.toThrow("500");
  });
});

describe("newApplicationFromForm", () => {
  it("reads the fields, one address per line", () => {
    const form = new FormData();
    form.set("key", " billing ");
    form.set("name", " Billing ");
    form.set("redirectUris", "https://billing.example/signin-oidc\r\n\n  https://billing.example/other  \n");
    form.set("postLogoutRedirectUris", "");

    expect(newApplicationFromForm(form)).toEqual({
      key: "billing",
      name: "Billing",
      redirectUris: ["https://billing.example/signin-oidc", "https://billing.example/other"],
      postLogoutRedirectUris: [],
    });
  });

  it("treats missing fields as empty, leaving the checks to AuthService", () => {
    expect(newApplicationFromForm(new FormData())).toEqual({ key: "", name: "", redirectUris: [], postLogoutRedirectUris: [] });
  });
});

describe("linesOf", () => {
  it("drops blank lines and surrounding spaces", () => {
    expect(linesOf(" a \n\n b\r\n")).toEqual(["a", "b"]);
  });
});

describe("canRotateSecret", () => {
  it("is not offered for auth, whose secret is the admin UI's own (a new one would lock the UI out)", () => {
    expect(canRotateSecret({ id: "1", key: "auth", name: "Kale Admin", isActive: true })).toBe(false);
    expect(canRotateSecret({ id: "2", key: "billing", name: "Billing", isActive: true })).toBe(true);
  });
});

describe("getApplication", () => {
  it("reads one application with its return addresses, escaping the id", async () => {
    const details = {
      id: "a/1", key: "billing", name: "Billing", isActive: true,
      redirectUris: ["https://billing.example/cb"], postLogoutRedirectUris: [],
    };
    const call = vi.fn<ApiCall>(async () => json(details));

    expect(await getApplication(call, "a/1")).toEqual(details);
    expect(call.mock.calls[0][0]).toBe("applications/a%2F1");
  });

  it("gives undefined for an unknown application", async () => {
    expect(await getApplication(async () => new Response(null, { status: 404 }), "a1")).toBeUndefined();
  });
});

describe("changeApplication", () => {
  it("puts the new name and addresses", async () => {
    const call = vi.fn<ApiCall>(async () => new Response(null, { status: 204 }));
    const changes = { name: "Billing", redirectUris: ["https://billing.example/cb"], postLogoutRedirectUris: [] };

    expect(await changeApplication(call, "a1", changes)).toEqual({ ok: true, value: undefined });
    expect(call.mock.calls[0][0]).toBe("applications/a1");
    expect(call.mock.calls[0][1]?.method).toBe("PUT");
    expect(JSON.parse(call.mock.calls[0][1]?.body as string)).toEqual(changes);
  });

  it("returns AuthService's reason (e.g. auth's own addresses)", async () => {
    const outcome = await changeApplication(async () => problem("The return addresses of 'auth' belong to the admin UI."), "a1",
      { name: "x", redirectUris: [], postLogoutRedirectUris: [] });

    expect(outcome).toEqual({ ok: false, refusal: "invalid", detail: "The return addresses of 'auth' belong to the admin UI." });
  });
});

describe("applicationChangesFromForm", () => {
  it("reads the name and one address per line", () => {
    const form = new FormData();
    form.set("name", " Billing ");
    form.set("redirectUris", "https://billing.example/a\n https://billing.example/b ");
    form.set("postLogoutRedirectUris", "");

    expect(applicationChangesFromForm(form)).toEqual({
      name: "Billing", redirectUris: ["https://billing.example/a", "https://billing.example/b"], postLogoutRedirectUris: [],
    });
  });
});
