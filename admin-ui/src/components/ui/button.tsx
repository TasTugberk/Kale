import type { ComponentProps } from "react";
import { cn } from "./cn";

const variants = {
  primary: "bg-brand-700 text-white shadow-sm hover:bg-brand-800",
  secondary: "border border-slate-300 bg-white text-slate-700 shadow-sm hover:bg-slate-50",
  ghost: "text-slate-600 hover:bg-slate-100 hover:text-slate-900",
  danger: "bg-red-600 text-white shadow-sm hover:bg-red-700",
  dangerOutline: "border border-red-200 bg-white text-red-700 shadow-sm hover:bg-red-50",
};

const sizes = {
  sm: "h-8 gap-1.5 px-2.5 text-xs",
  md: "h-9 gap-2 px-3.5 text-sm",
};

export type ButtonVariant = keyof typeof variants;

export interface ButtonProps extends ComponentProps<"button"> {
  variant?: ButtonVariant;
  size?: keyof typeof sizes;
}

/** Without a `type`, a button inside a form submits it, like a plain HTML button. */
export function Button({ variant = "primary", size = "md", className, ...props }: ButtonProps) {
  return (
    <button
      className={cn(
        "inline-flex shrink-0 cursor-pointer items-center justify-center rounded-md font-medium whitespace-nowrap transition-colors",
        "focus-visible:outline-2 focus-visible:outline-offset-2 focus-visible:outline-brand-500 disabled:pointer-events-none disabled:opacity-50",
        "[&_svg]:size-4",
        variants[variant],
        sizes[size],
        className,
      )}
      {...props}
    />
  );
}

/** A link that looks like a button (e.g. "Roles" on an application's row). */
export function ButtonLink({ variant = "secondary", size = "sm", className, ...props }: ComponentProps<"a"> & { variant?: ButtonVariant; size?: keyof typeof sizes }) {
  return (
    <a
      className={cn(
        "inline-flex shrink-0 items-center justify-center rounded-md font-medium whitespace-nowrap no-underline transition-colors [&_svg]:size-4",
        variants[variant],
        sizes[size],
        // Links are blue by default (globals.css); a button link keeps its variant's colour.
        variant === "primary" || variant === "danger" ? "text-white hover:text-white" : "hover:text-slate-900",
        className,
      )}
      {...props}
    />
  );
}
