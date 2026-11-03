import type { ComponentProps, ReactNode } from "react";
import { cn } from "./cn";

const control =
  "block w-full rounded-md border border-slate-300 bg-white px-3 py-2 text-sm text-slate-900 shadow-sm placeholder:text-slate-400 " +
  "focus:border-brand-500 focus:outline-none focus:ring-2 focus:ring-brand-500/20 read-only:bg-slate-50 read-only:text-slate-500 disabled:opacity-60";

/**
 * A labelled control with an optional hint under it. The label wraps the control, so clicking it focuses the control;
 * the hint stays outside it, or screen readers would read it as part of the control's name.
 */
export function Field({ label, hint, children, className }: { label: string; hint?: ReactNode; children: ReactNode; className?: string }) {
  return (
    <div className={cn("grid gap-1.5", className)}>
      <label className="grid gap-1.5">
        <span className="text-sm font-medium text-slate-700">{label}</span>
        {children}
      </label>
      {hint && <p className="text-xs text-slate-500">{hint}</p>}
    </div>
  );
}

export function Input({ className, ...props }: ComponentProps<"input">) {
  return <input className={cn(control, "h-9 py-0", className)} {...props} />;
}

export function Textarea({ className, ...props }: ComponentProps<"textarea">) {
  return <textarea className={cn(control, "font-mono text-xs", className)} {...props} />;
}

/** A plain HTML select: it posts with its form like any field. */
export function Select({ className, ...props }: ComponentProps<"select">) {
  return <select className={cn(control, "h-9 w-auto py-0 pr-8", className)} {...props} />;
}
