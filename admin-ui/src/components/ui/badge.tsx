import type { ReactNode } from "react";
import { cn } from "./cn";

const tones = {
  green: "bg-emerald-50 text-emerald-700 ring-emerald-600/20",
  grey: "bg-slate-100 text-slate-600 ring-slate-500/20",
  amber: "bg-amber-50 text-amber-800 ring-amber-600/20",
  red: "bg-red-50 text-red-700 ring-red-600/20",
  blue: "bg-brand-50 text-brand-700 ring-brand-600/20",
};

export function Badge({ tone = "grey", children, className }: { tone?: keyof typeof tones; children: ReactNode; className?: string }) {
  return (
    <span className={cn("inline-flex items-center rounded-md px-2 py-0.5 text-xs font-medium whitespace-nowrap ring-1 ring-inset", tones[tone], className)}>
      {children}
    </span>
  );
}

/** An application key, operation name or other identifier, in a fixed-width font. */
export function Code({ children, className }: { children: ReactNode; className?: string }) {
  return <code className={cn("rounded bg-slate-100 px-1.5 py-0.5 font-mono text-xs text-slate-800", className)}>{children}</code>;
}
