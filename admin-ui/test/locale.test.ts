import { describe, expect, it } from "vitest";
import { pickLocale } from "../src/i18n/locale";

describe("pickLocale", () => {
  it("uses the admin's choice first", () => {
    expect(pickLocale("en", "tr-TR")).toBe("en");
    expect(pickLocale("tr", "en-US")).toBe("tr");
  });

  it("otherwise English only when the browser asks for it first, else Turkish", () => {
    expect(pickLocale(undefined, "en-US,en;q=0.9")).toBe("en");
    expect(pickLocale(undefined, "tr-TR,en;q=0.5")).toBe("tr");
    expect(pickLocale("fr", "")).toBe("tr");
  });
});
