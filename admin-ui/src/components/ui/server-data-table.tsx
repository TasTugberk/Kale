"use client";

import { useState, type ReactNode } from "react";
import { useRouter } from "next/navigation";
import { useTranslations } from "next-intl";
import { ArrowDown, ArrowUp, ArrowUpDown, ChevronLeft, ChevronRight, Search } from "lucide-react";
import { pageCount, type ListQuery } from "@/lib/paging";
import { cn } from "./cn";

/** A column of a ServerDataTable: its title, its cell, and the admin API's sort for it (if it has one). */
export interface ServerColumn<T> {
  id: string;
  header: ReactNode;
  cell: (row: T) => ReactNode;
  /** The admin API's ?sort= value; leave it out for a column the API can't sort by. */
  sort?: string;
}

/**
 * A list the admin API searches, sorts and pages (users, open sessions), for lists too long to load whole. The
 * search box, the sortable titles and the page buttons only change the page's address (`href`), so the server page
 * reads the new one, and Back, reload and bookmarks work. Looks like DataTable.
 */
export function ServerDataTable<T>({ columns, rows, rowId, total, pageSize, query, href, empty }: {
  columns: ServerColumn<T>[];
  rows: T[];
  rowId: (row: T) => string;
  /** How many items match in all (not only on this page). */
  total: number;
  pageSize: number;
  query: ListQuery;
  /** The address of the list for another query (search, page or sort), keeping the page's filters. */
  href: (query: ListQuery) => string;
  /** Shown when the list is empty without a search. */
  empty: ReactNode;
}) {
  const t = useTranslations("Table");

  if (total === 0 && !query.search) {
    return <p className="px-5 py-10 text-center text-sm text-slate-500">{empty}</p>;
  }

  const pages = pageCount(total, pageSize);
  const first = total === 0 ? 0 : (query.page - 1) * pageSize + 1;
  const last = Math.min(total, query.page * pageSize);

  return (
    <div>
      <div className="flex flex-wrap items-center gap-3 border-b border-slate-200 px-5 py-3">
        {/* Keyed by the address's search, so Back or a link shows that search in the box again. */}
        <SearchBox key={query.search} query={query} href={href} />
      </div>
      <div className="overflow-x-auto">
        <table className="w-full text-left text-sm">
          <thead className="bg-slate-50 text-xs font-medium tracking-wide text-slate-500 uppercase">
            <tr>
              {columns.map(column => (
                <SortableHeader key={column.id} column={column} query={query} href={href} />
              ))}
            </tr>
          </thead>
          <tbody className="divide-y divide-slate-100">
            {rows.map(row => (
              <tr key={rowId(row)} className="hover:bg-slate-50/60">
                {columns.map(column => <td key={column.id} className="px-5 py-3 align-middle">{column.cell(row)}</td>)}
              </tr>
            ))}
          </tbody>
        </table>
        {rows.length === 0 && <p className="px-5 py-10 text-center text-sm text-slate-500">{t("noMatches")}</p>}
      </div>
      {total > pageSize && (
        <div className="flex items-center justify-between gap-3 border-t border-slate-200 px-5 py-3 text-sm text-slate-500">
          <span>{t("range", { first, last, total })}</span>
          <div className="flex gap-1">
            <PageLink label={t("previous")} href={query.page > 1 ? href({ ...query, page: query.page - 1 }) : undefined}>
              <ChevronLeft className="size-4" aria-hidden />
            </PageLink>
            <PageLink label={t("next")} href={query.page < pages ? href({ ...query, page: query.page + 1 }) : undefined}>
              <ChevronRight className="size-4" aria-hidden />
            </PageLink>
          </div>
        </div>
      )}
    </div>
  );
}

// The search is sent with Enter (one request, not one per key); a new search starts again from the first page.
function SearchBox({ query, href }: { query: ListQuery; href: (query: ListQuery) => string }) {
  const t = useTranslations("Table");
  const router = useRouter();
  const [search, setSearch] = useState(query.search);
  return (
    <form
      role="search"
      className="relative w-full max-w-xs"
      onSubmit={event => {
        event.preventDefault();
        router.push(href({ ...query, search: search.trim(), page: 1 }));
      }}
    >
      <label>
        <span className="sr-only">{t("search")}</span>
        <Search className="pointer-events-none absolute top-1/2 left-2.5 size-4 -translate-y-1/2 text-slate-400" aria-hidden />
        <input
          type="search"
          value={search}
          onChange={event => setSearch(event.target.value)}
          placeholder={t("searchAndEnter")}
          className="h-9 w-full rounded-md border border-slate-300 bg-white pr-3 pl-8 text-sm shadow-sm focus:border-brand-500 focus:ring-2 focus:ring-brand-500/20 focus:outline-none"
        />
      </label>
    </form>
  );
}

// A title the API can sort by is a link: ascending first, then descending; a new sort starts from the first page.
function SortableHeader<T>({ column, query, href }: { column: ServerColumn<T>; query: ListQuery; href: (query: ListQuery) => string }) {
  if (!column.sort) {
    return <th scope="col" className="px-5 py-2.5 font-medium whitespace-nowrap">{column.header}</th>;
  }

  const sorted = query.sort === column.sort ? (query.desc ? "desc" : "asc") : undefined;
  const next = { ...query, sort: column.sort, desc: sorted === "asc", page: 1 };
  return (
    <th
      scope="col"
      className="px-5 py-2.5 font-medium whitespace-nowrap"
      aria-sort={sorted === "asc" ? "ascending" : sorted === "desc" ? "descending" : undefined}
    >
      <a href={href(next)} className="inline-flex items-center gap-1 text-slate-500 uppercase no-underline hover:text-slate-800">
        {column.header}
        {sorted === "asc" ? <ArrowUp className="size-3.5" aria-hidden />
          : sorted === "desc" ? <ArrowDown className="size-3.5" aria-hidden />
            : <ArrowUpDown className="size-3.5 opacity-40" aria-hidden />}
      </a>
    </th>
  );
}

function PageLink({ label, href, children }: { label: string; href: string | undefined; children: ReactNode }) {
  const look = "rounded-md border border-slate-300 bg-white p-1.5 text-slate-600 shadow-sm";
  if (!href) {
    return <span aria-label={label} title={label} aria-disabled="true" className={cn(look, "opacity-40")}>{children}</span>;
  }

  return <a href={href} aria-label={label} title={label} className={cn(look, "hover:bg-slate-50")}>{children}</a>;
}
