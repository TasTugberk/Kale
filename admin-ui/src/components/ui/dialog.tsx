"use client";

import type { ReactNode } from "react";
import { useTranslations } from "next-intl";
import { Dialog as RadixDialog } from "radix-ui";
import { X } from "lucide-react";
import { cn } from "./cn";

/**
 * A modal window opened by `trigger` (a button), e.g. "New application". The caller owns `open`, so a form inside
 * can close it after its change was made, and keep it open (with the reason) after a refusal.
 */
export function Dialog({ open, onOpenChange, trigger, title, description, children, wide }: {
  open: boolean;
  onOpenChange: (open: boolean) => void;
  /** Leave it out for a dialog opened by something else (e.g. a new secret, after it was made). */
  trigger?: ReactNode;
  title: string;
  description?: ReactNode;
  children: ReactNode;
  wide?: boolean;
}) {
  const t = useTranslations("Forms");
  return (
    <RadixDialog.Root open={open} onOpenChange={onOpenChange}>
      {trigger && <RadixDialog.Trigger asChild>{trigger}</RadixDialog.Trigger>}
      <RadixDialog.Portal>
        <RadixDialog.Overlay className="fixed inset-0 z-40 bg-slate-900/40" />
        <RadixDialog.Content
          // Without a description, tell Radix there is none (it warns otherwise).
          {...(description ? {} : { "aria-describedby": undefined })}
          className={cn(
            "fixed top-1/2 left-1/2 z-50 max-h-[90vh] w-[calc(100vw-2rem)] -translate-x-1/2 -translate-y-1/2 overflow-y-auto",
            "rounded-lg bg-white p-6 shadow-xl focus:outline-none",
            wide ? "max-w-2xl" : "max-w-lg",
          )}
        >
          <div className="flex items-start justify-between gap-4">
            <div>
              <RadixDialog.Title className="text-lg font-semibold text-slate-900">{title}</RadixDialog.Title>
              {description && <RadixDialog.Description className="mt-1 text-sm text-slate-500">{description}</RadixDialog.Description>}
            </div>
            <RadixDialog.Close asChild>
              <button type="button" aria-label={t("close")} className="cursor-pointer rounded-md p-1 text-slate-400 hover:bg-slate-100 hover:text-slate-700">
                <X className="size-5" aria-hidden />
              </button>
            </RadixDialog.Close>
          </div>
          <div className="mt-5">{children}</div>
        </RadixDialog.Content>
      </RadixDialog.Portal>
    </RadixDialog.Root>
  );
}

/** The buttons at the bottom of a dialog's form, on the right. */
export function DialogActions({ children }: { children: ReactNode }) {
  return <div className="mt-6 flex flex-wrap justify-end gap-2">{children}</div>;
}
