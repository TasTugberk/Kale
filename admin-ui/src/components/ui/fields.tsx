"use client";

import { createContext, useContext, useId, type ComponentProps, type ReactNode } from "react";
import { cn } from "./cn";

const control =
  "block w-full rounded-md border border-slate-300 bg-white px-3 py-2 text-sm text-slate-900 shadow-sm placeholder:text-slate-400 " +
  "focus:border-brand-500 focus:outline-none focus:ring-2 focus:ring-brand-500/20 read-only:bg-slate-50 read-only:text-slate-500 disabled:opacity-60";

// The id of the hint of the Field a control sits in, so the control can name it as its description.
const HintId = createContext<string | undefined>(undefined);

/**
 * A labelled control with an optional hint under it. The label wraps the control, so clicking it focuses the control;
 * the hint stays outside it, or screen readers would read it as part of the control's name. It is the control's
 * description instead (aria-describedby), read when the control gets focus.
 */
export function Field({ label, hint, children, className }: { label: string; hint?: ReactNode; children: ReactNode; className?: string }) {
  const hintId = useId();
  return (
    <div className={cn("grid gap-1.5", className)}>
      <label className="grid gap-1.5">
        <span className="text-sm font-medium text-slate-700">{label}</span>
        <HintId value={hint ? hintId : undefined}>{children}</HintId>
      </label>
      {hint && <p id={hintId} className="text-xs text-slate-500">{hint}</p>}
    </div>
  );
}

export function Input({ className, ...props }: ComponentProps<"input">) {
  const hintId = useContext(HintId);
  return <input aria-describedby={hintId} className={cn(control, "h-9 py-0", className)} {...props} />;
}

export function Textarea({ className, ...props }: ComponentProps<"textarea">) {
  const hintId = useContext(HintId);
  return <textarea aria-describedby={hintId} className={cn(control, "font-mono text-xs", className)} {...props} />;
}

/** A plain HTML select: it posts with its form like any field. */
export function Select({ className, ...props }: ComponentProps<"select">) {
  const hintId = useContext(HintId);
  return <select aria-describedby={hintId} className={cn(control, "h-9 w-auto py-0 pr-8", className)} {...props} />;
}
