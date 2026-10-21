import { describe, expect, it } from "vitest";
import { refusalMessage } from "../src/lib/admin-requests";

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
