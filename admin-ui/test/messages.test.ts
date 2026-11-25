import { describe, expect, it } from "vitest";
import { createTranslator } from "next-intl";
import en from "../messages/en.json";
import tr from "../messages/tr.json";
import { refusalMessage } from "../src/lib/admin-requests";

// "Section.key" for every text, so a missing translation shows up here, not as a raw key on a page.
function keysOf(messages: object, prefix = ""): string[] {
  return Object.entries(messages).flatMap(([key, value]) =>
    typeof value === "object" && value !== null ? keysOf(value, `${prefix}${key}.`) : [`${prefix}${key}`]);
}

// Every value a refusal text may use (AuthService's RefusalCodes says which code sends which).
const refusalValues = {
  field: "isActive", key: "billing", max: "200", address: "https://x.test/#a", name: "Readers",
  operations: "billing.Old", userName: "deniz", min: "12", count: "2",
};

describe("messages", () => {
  it("has every text in both Turkish and English", () => {
    expect(keysOf(tr).sort()).toEqual(keysOf(en).sort());
  });

  it.each([["tr", tr], ["en", en]] as const)("formats every refusal text in %s with its values", (locale, messages) => {
    const t = createTranslator({ locale, messages, namespace: "Refusals", onError: error => { throw error; } });

    // next-intl types each key's values from the messages; this walks every key, so it takes them all as text.
    const format = t as unknown as (key: string, values: Record<string, string>) => string;
    for (const key of keysOf(messages.Refusals)) {
      expect(format(key, refusalValues)).not.toMatch(/[{}]/);
    }
  });

  it("shows a refusal from AuthService in Turkish with the real texts", () => {
    const t = createTranslator({ locale: "tr", messages: tr, namespace: "Forms" });
    const refusals = createTranslator({ locale: "tr", messages: tr, namespace: "Refusals" });
    const reasons = [{ code: "role.name-taken", values: { name: "Okurlar" }, detail: "This application already has a role 'Okurlar'." }];

    const message = refusalMessage({ ok: false, refusal: "invalid", detail: "English.", reasons }, t as never, refusals as never);

    expect(message).toBe("AuthService değişikliği reddetti: Bu uygulamada “Okurlar” adlı bir rol zaten var.");
  });
});
