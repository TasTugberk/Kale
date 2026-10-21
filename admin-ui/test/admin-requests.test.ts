import { describe, expect, it } from "vitest";
import { afterSubmit, refusalMessage } from "../src/lib/admin-requests";

// Stands in for next-intl's t(): shows which text was picked, with its values.
const t = (key: string, values?: Record<string, string>) => (values ? `${key}:${JSON.stringify(values)}` : key);

describe("refusalMessage", () => {
  it("shows nothing before the first submit or after a success", () => {
    expect(refusalMessage(undefined, t)).toBeUndefined();
    expect(refusalMessage({ ok: true, value: 1 }, t)).toBeUndefined();
  });

  it("gives AuthService's reason when there is one", () => {
    expect(refusalMessage({ ok: false, refusal: "invalid", detail: "Key taken." }, t)).toBe('refused:{"detail":"Key taken."}');
  });

  it("says the input was refused when AuthService gave no reason", () => {
    expect(refusalMessage({ ok: false, refusal: "invalid", detail: undefined }, t)).toBe("invalid");
  });

  it("says the item is gone", () => {
    expect(refusalMessage({ ok: false, refusal: "not-found" }, t)).toBe("notFound");
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
