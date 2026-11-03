"use client";

import { useState, type ReactNode } from "react";
import { useTranslations } from "next-intl";
import {
  columnFilteringFeature, createFilteredRowModel, createPaginatedRowModel, createSortedRowModel, filterFns,
  globalFilteringFeature, rowPaginationFeature, rowSortingFeature, sortFns, tableFeatures, useTable, type ColumnDef,
  type RowData, type SortingState,
} from "@tanstack/react-table";
import { ArrowDown, ArrowUp, ArrowUpDown, ChevronLeft, ChevronRight, Search } from "lucide-react";
import { cn } from "./cn";

// Sorting, searching and paging, all in the browser: the admin API returns whole lists, and they are small.
const features = tableFeatures({
  columnFilteringFeature,
  globalFilteringFeature,
  rowSortingFeature,
  rowPaginationFeature,
  filteredRowModel: createFilteredRowModel(),
  sortedRowModel: createSortedRowModel(),
  paginatedRowModel: createPaginatedRowModel(),
  filterFns,
  sortFns,
});

/**
 * A column of a DataTable. Columns with an `accessorKey` (or `accessorFn`) are searched and sortable; give the
 * others (e.g. row actions) an `id` and `enableSorting: false`.
 */
export type DataColumn<T extends RowData> = ColumnDef<typeof features, T>;

/**
 * A list as a grid: a search box over the searchable columns, sorting by clicking a column's title, and pages of
 * `pageSize` rows. Defined in client components, because columns hold functions (the cells), which a server page
 * can't hand over.
 */
export function DataTable<T extends RowData>({ columns, data, empty, pageSize = 10, initialSorting = [], toolbar }: {
  columns: DataColumn<T>[];
  data: T[];
  /** Shown when there are no rows at all (not when the search finds none). */
  empty: ReactNode;
  pageSize?: number;
  initialSorting?: SortingState;
  /** More controls next to the search box (e.g. a filter). */
  toolbar?: ReactNode;
}) {
  const t = useTranslations("Table");
  const [globalFilter, setGlobalFilter] = useState("");
  const table = useTable({
    features,
    columns,
    data,
    initialState: { sorting: initialSorting, pagination: { pageIndex: 0, pageSize } },
    state: { globalFilter },
    onGlobalFilterChange: setGlobalFilter,
    globalFilterFn: "includesString",
  });

  const found = table.getFilteredRowModel().rows.length;
  const { pageIndex } = table.state.pagination;
  const first = found === 0 ? 0 : pageIndex * pageSize + 1;
  const last = Math.min(found, (pageIndex + 1) * pageSize);

  return (
    <div>
      <div className="flex flex-wrap items-center gap-3 border-b border-slate-200 px-5 py-3">
        <label className="relative w-full max-w-xs">
          <span className="sr-only">{t("search")}</span>
          <Search className="pointer-events-none absolute top-1/2 left-2.5 size-4 -translate-y-1/2 text-slate-400" aria-hidden />
          <input
            type="search"
            value={globalFilter}
            onChange={event => setGlobalFilter(event.target.value)}
            placeholder={t("search")}
            className="h-9 w-full rounded-md border border-slate-300 bg-white pr-3 pl-8 text-sm shadow-sm focus:border-brand-500 focus:ring-2 focus:ring-brand-500/20 focus:outline-none"
          />
        </label>
        {toolbar}
      </div>
      <div className="overflow-x-auto">
        <table className="w-full text-left text-sm">
          <thead className="bg-slate-50 text-xs font-medium tracking-wide text-slate-500 uppercase">
            {table.getHeaderGroups().map(headerGroup => (
              <tr key={headerGroup.id}>
                {headerGroup.headers.map(header => {
                  const sorted = header.column.getIsSorted();
                  return (
                    <th
                      key={header.id}
                      scope="col"
                      className="px-5 py-2.5 font-medium whitespace-nowrap"
                      aria-sort={sorted === "asc" ? "ascending" : sorted === "desc" ? "descending" : undefined}
                    >
                      {header.column.getCanSort()
                        ? (
                          <button
                            type="button"
                            onClick={header.column.getToggleSortingHandler()}
                            className="inline-flex cursor-pointer items-center gap-1 uppercase hover:text-slate-800"
                          >
                            <table.FlexRender header={header} />
                            {sorted === "asc" ? <ArrowUp className="size-3.5" aria-hidden />
                              : sorted === "desc" ? <ArrowDown className="size-3.5" aria-hidden />
                                : <ArrowUpDown className="size-3.5 opacity-40" aria-hidden />}
                          </button>
                        )
                        : <table.FlexRender header={header} />}
                    </th>
                  );
                })}
              </tr>
            ))}
          </thead>
          <tbody className="divide-y divide-slate-100">
            {table.getRowModel().rows.map(row => (
              <tr key={row.id} className="hover:bg-slate-50/60">
                {row.getAllCells().map(cell => (
                  <td key={cell.id} className="px-5 py-3 align-middle">
                    <table.FlexRender cell={cell} />
                  </td>
                ))}
              </tr>
            ))}
          </tbody>
        </table>
        {data.length === 0 && <p className="px-5 py-10 text-center text-sm text-slate-500">{empty}</p>}
        {data.length > 0 && found === 0 && <p className="px-5 py-10 text-center text-sm text-slate-500">{t("noMatches")}</p>}
      </div>
      {found > pageSize && (
        <div className="flex items-center justify-between gap-3 border-t border-slate-200 px-5 py-3 text-sm text-slate-500">
          <span>{t("range", { first, last, total: found })}</span>
          <div className="flex gap-1">
            <PageButton label={t("previous")} disabled={!table.getCanPreviousPage()} onClick={() => table.previousPage()}>
              <ChevronLeft className="size-4" aria-hidden />
            </PageButton>
            <PageButton label={t("next")} disabled={!table.getCanNextPage()} onClick={() => table.nextPage()}>
              <ChevronRight className="size-4" aria-hidden />
            </PageButton>
          </div>
        </div>
      )}
    </div>
  );
}

function PageButton({ label, disabled, onClick, children }: { label: string; disabled: boolean; onClick: () => void; children: ReactNode }) {
  return (
    <button
      type="button"
      aria-label={label}
      title={label}
      disabled={disabled}
      onClick={onClick}
      className={cn("cursor-pointer rounded-md border border-slate-300 bg-white p-1.5 text-slate-600 shadow-sm hover:bg-slate-50", "disabled:pointer-events-none disabled:opacity-40")}
    >
      {children}
    </button>
  );
}
