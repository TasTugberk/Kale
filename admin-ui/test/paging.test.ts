import { describe, expect, it } from "vitest";
import { apiPath, listHref, listQueryFrom, pageCount } from "../src/lib/paging";

describe("listQueryFrom", () => {
  it("reads the address's search, page and sort", () => {
    expect(listQueryFrom({ search: " deniz ", page: "3", sort: "displayName", desc: "1" }, ["userName", "displayName"]))
      .toEqual({ search: "deniz", page: 3, sort: "displayName", desc: true });
  });

  it("starts from page 1, no search and the server's own order when the address has none", () => {
    expect(listQueryFrom({})).toEqual({ search: "", page: 1, sort: undefined, desc: false });
  });

  it("treats a page that isn't a positive whole number as page 1 (a hand-edited address)", () => {
    expect(listQueryFrom({ page: "x" }).page).toBe(1);
    expect(listQueryFrom({ page: "0" }).page).toBe(1);
    expect(listQueryFrom({ page: "2.5" }).page).toBe(1);
  });

  it("keeps only the sorts the list offers", () => {
    expect(listQueryFrom({ sort: "password" }, ["userName"]).sort).toBeUndefined();
    expect(listQueryFrom({ sort: "userName" }, ["userName"]).sort).toBe("userName");
  });
});

describe("apiPath", () => {
  it("asks the admin API for one page, with the filters, escaped", () => {
    const path = apiPath("sessions", { search: "a&b", page: 2, sort: undefined, desc: false }, 25, { userId: "u/1", applicationId: undefined });

    expect(path).toBe("sessions?userId=u%2F1&search=a%26b&page=2&pageSize=25");
  });

  it("sends the sort and its direction", () => {
    expect(apiPath("users", { search: "", page: 1, sort: "displayName", desc: true }, 25))
      .toBe("users?page=1&pageSize=25&sort=displayName&desc=true");
  });
});

describe("listHref", () => {
  it("leaves the defaults out of the address, so the plain list stays a plain link", () => {
    expect(listHref("/users", { search: "", page: 1, sort: undefined, desc: false })).toBe("/users");
  });

  it("keeps the filters and what differs from the defaults", () => {
    expect(listHref("/sessions", { search: "deniz", page: 3, sort: "userName", desc: true }, { userId: "u1" }))
      .toBe("/sessions?userId=u1&search=deniz&page=3&sort=userName&desc=1");
  });
});

describe("pageCount", () => {
  it("is at least one page, even for an empty list", () => {
    expect(pageCount(0, 25)).toBe(1);
    expect(pageCount(25, 25)).toBe(1);
    expect(pageCount(26, 25)).toBe(2);
  });
});
