import { describe, expect, it } from "vitest";
import en from "../messages/en.json";
import tr from "../messages/tr.json";

// "Section.key" for every text, so a missing translation shows up here, not as a raw key on a page.
function keysOf(messages: object, prefix = ""): string[] {
  return Object.entries(messages).flatMap(([key, value]) =>
    typeof value === "object" && value !== null ? keysOf(value, `${prefix}${key}.`) : [`${prefix}${key}`]);
}

describe("messages", () => {
  it("has every text in both Turkish and English", () => {
    expect(keysOf(tr).sort()).toEqual(keysOf(en).sort());
  });
});
