import { describe, expect, it } from "vitest";
import { afterSubmit, change, find, nothing, refusalMessage, type RefusalReason } from "../src/lib/admin-requests";

// Stands in for next-intl's t(): shows which text was picked, with its values.
const t = (key: string, values?: Record<string, string>) => (values ? `${key}:${JSON.stringify(values)}` : key);

// The "Refusals" texts: only role.name-taken has one here.
const refusals = Object.assign(
  (key: string, values?: Record<string, string>) => `<${key} ${JSON.stringify(values)}>`,
  { has: (key: string) => key === "role.name-taken" },
);

describe("refusalMessage", () => {
  it("shows nothing before the first submit or after a success", () => {
    expect(refusalMessage(undefined, t, refusals)).toBeUndefined();
    expect(refusalMessage({ ok: true, value: 1 }, t, refusals)).toBeUndefined();
  });

  it("gives AuthService's reason when there is one", () => {
    expect(refusalMessage({ ok: false, refusal: "invalid", detail: "Key taken." }, t, refusals)).toBe('refused:{"detail":"Key taken."}');
  });

  it("shows each reason in the admin's language by its code, with its values", () => {
    const reasons = [{ code: "role.name-taken", values: { name: "Readers" }, detail: "This application already has a role 'Readers'." }];

    const message = refusalMessage({ ok: false, refusal: "invalid", detail: "English.", reasons }, t, refusals);

    expect(message).toBe(t("refused", { detail: refusals("role.name-taken", { name: "Readers" }) }));
  });

  it("keeps AuthService's English for a code it has no text for, next to the ones it has", () => {
    const reasons: RefusalReason[] = [
      { code: "identity.SomethingNew", values: {}, detail: "Something new." },
      { code: "role.name-taken", values: { name: "R" }, detail: "Taken." },
    ];

    const message = refusalMessage({ ok: false, refusal: "invalid", detail: "English.", reasons }, t, refusals);

    expect(message).toBe(t("refused", { detail: `Something new. ${refusals("role.name-taken", { name: "R" })}` }));
  });

  it("says the input was refused when AuthService gave no reason", () => {
    expect(refusalMessage({ ok: false, refusal: "invalid", detail: undefined }, t, refusals)).toBe("invalid");
  });

  it("says the item is gone", () => {
    expect(refusalMessage({ ok: false, refusal: "not-found" }, t, refusals)).toBe("notFound");
  });
});

describe("change", () => {
  function refusal(body: unknown) {
    return async () => new Response(JSON.stringify(body), { status: 400 });
  }

  it("reads a refusal's reasons with their codes and values", async () => {
    const reasons = [{ code: "role.name-taken", values: { name: "Readers" }, detail: "Taken." }];

    expect(await change(refusal({ status: 400, detail: "Taken.", reasons }), "roles", { method: "POST" }, nothing))
      .toEqual({ ok: false, refusal: "invalid", detail: "Taken.", reasons });
  });

  it("leaves out reasons that aren't well formed", async () => {
    const reasons = [{ code: 1 }, "text", { code: "role.name-taken", detail: "Taken." }, { code: "x", detail: "X.", values: { n: 1 } }];

    expect(await change(refusal({ status: 400, detail: "Taken.", reasons }), "roles", { method: "POST" }, nothing))
      .toEqual({ ok: false, refusal: "invalid", detail: "Taken.", reasons: [
        { code: "role.name-taken", values: {}, detail: "Taken." },
        { code: "x", values: {}, detail: "X." },
      ] });
  });
});

describe("afterSubmit", () => {
  const submitted = { name: "Readers" };

  it("keeps what the admin sent after a refusal, to correct it", () => {
    const refused = { ok: false as const, refusal: "invalid" as const, detail: "taken" };

    expect(afterSubmit(refused, submitted)).toEqual({ outcome: refused, submitted });
  });

  it("forgets it after a success, so the form shows the saved state", () => {
    const done = { ok: true as const, value: undefined };

    expect(afterSubmit(done, submitted)).toEqual({ outcome: done, submitted: undefined });
  });
});

describe("find", () => {
  it("reads the item", async () => {
    const found = await find<{ id: string }>(async () => new Response(JSON.stringify({ id: "1" })), "users/1");

    expect(found).toEqual({ id: "1" });
  });

  it("gives undefined for an unknown id (the page shows not found)", async () => {
    expect(await find(async () => new Response(null, { status: 404 }), "users/1")).toBeUndefined();
  });

  it("throws on other failures", async () => {
    await expect(find(async () => new Response(null, { status: 500 }), "users/1")).rejects.toThrow("500");
  });
});
