/**
 * Lists that can grow large (users, open sessions) are searched, sorted and paged by the admin API, not in the
 * browser. Which page is shown lives in the page's address (?search=&page=&sort=&desc=), so Back, reload and a
 * bookmark show the same page. Free of Next.js: unit-tested.
 */

/** One page of a list, as the admin API answers: the items and how many match in all. */
export interface Page<T> {
  items: T[];
  total: number;
  page: number;
  pageSize: number;
}

/** What the address asks for. No sort means the admin API's own order. */
export interface ListQuery {
  search: string;
  page: number;
  sort: string | undefined;
  desc: boolean;
}

/** Rows per page in the admin UI. */
export const uiPageSize = 25;

/** The largest page the admin API gives (its PageRequest.MaxPageSize). */
export const maxPageSize = 200;

/** Filters a list keeps besides its query (e.g. ?userId=); undefined ones are left out. */
export type Filters = Record<string, string | undefined>;

/**
 * Reads the address. Anything unusable (a page that isn't a positive whole number, a sort the list doesn't offer)
 * falls back to the default, as for a hand-edited address.
 */
export function listQueryFrom(
  params: { search?: string; page?: string; sort?: string; desc?: string }, sortable: string[] = [],
): ListQuery {
  const page = Number(params.page);
  return {
    search: params.search?.trim() ?? "",
    page: Number.isInteger(page) && page >= 1 ? page : 1,
    sort: params.sort && sortable.includes(params.sort) ? params.sort : undefined,
    desc: params.desc === "1" || params.desc === "true",
  };
}

/** The admin API path for one page: filters first, then search, page, size and sort. */
export function apiPath(path: string, query: ListQuery, pageSize: number, filters: Filters = {}): string {
  const params = withFilters(filters);
  if (query.search) {
    params.set("search", query.search);
  }

  params.set("page", String(query.page));
  params.set("pageSize", String(pageSize));
  if (query.sort) {
    params.set("sort", query.sort);
    if (query.desc) {
      params.set("desc", "true");
    }
  }

  return `${path}?${params}`;
}

/** The admin UI address for a page of the list; the defaults are left out, so the first page is the plain path. */
export function listHref(path: string, query: ListQuery, filters: Filters = {}): string {
  const params = withFilters(filters);
  if (query.search) {
    params.set("search", query.search);
  }

  if (query.page > 1) {
    params.set("page", String(query.page));
  }

  if (query.sort) {
    params.set("sort", query.sort);
    if (query.desc) {
      params.set("desc", "1");
    }
  }

  const search = params.toString();
  return search ? `${path}?${search}` : path;
}

/** How many pages `total` items fill; at least one, so an empty list still has its (empty) first page. */
export function pageCount(total: number, pageSize: number): number {
  return Math.max(1, Math.ceil(total / pageSize));
}

function withFilters(filters: Filters): URLSearchParams {
  const params = new URLSearchParams();
  for (const [name, value] of Object.entries(filters)) {
    if (value) {
      params.set(name, value);
    }
  }

  return params;
}
