import type { ReactNode } from "react";
import { ChevronLeft } from "lucide-react";

/** A page's title, with an optional link back to its list, a line under it and actions on the right. */
export function PageHeader({ title, description, back, actions }: {
  title: ReactNode;
  description?: ReactNode;
  back?: { href: string; label: string };
  actions?: ReactNode;
}) {
  return (
    <div className="mb-6">
      {back && (
        <a href={back.href} className="mb-2 inline-flex items-center gap-1 text-sm text-slate-500 no-underline hover:text-slate-800">
          <ChevronLeft className="size-4" aria-hidden />
          {back.label}
        </a>
      )}
      <div className="flex flex-wrap items-end justify-between gap-4">
        <div className="min-w-0">
          <h1 className="text-2xl font-semibold tracking-tight text-slate-900">{title}</h1>
          {description && <p className="mt-1 max-w-3xl text-sm text-slate-500">{description}</p>}
        </div>
        {actions && <div className="flex flex-wrap items-center gap-2">{actions}</div>}
      </div>
    </div>
  );
}
