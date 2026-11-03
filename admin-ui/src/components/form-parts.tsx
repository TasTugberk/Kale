"use client";

import { useRef, useState, type ReactNode } from "react";
import { useTranslations } from "next-intl";
import { AlertDialog } from "radix-ui";
import { CheckCircle2, Copy, TriangleAlert } from "lucide-react";
import { refusalMessage, type FormState } from "@/lib/admin-requests";
import { Button, type ButtonVariant } from "./ui/button";
import { cn } from "./ui/cn";

/** Why a change didn't happen (nothing before the first submit, or after a success). */
export function Refusal({ state, className }: { state: FormState<unknown>; className?: string }) {
  const t = useTranslations("Forms");
  const message = refusalMessage(state, t);
  if (!message) {
    return null;
  }

  return (
    <p role="alert" className={cn("flex items-start gap-2 rounded-md bg-red-50 px-3 py-2 text-sm text-red-700", className)}>
      <TriangleAlert className="mt-0.5 size-4 shrink-0" aria-hidden />
      {message}
    </p>
  );
}

/** For changes that don't otherwise show on the page (e.g. a select that keeps its value). */
export function Saved({ state }: { state: FormState<unknown> }) {
  const t = useTranslations("Forms");
  if (!state?.ok) {
    return null;
  }

  return (
    <p role="status" className="inline-flex items-center gap-1 text-sm text-emerald-700">
      <CheckCircle2 className="size-4" aria-hidden />
      {t("saved")}
    </p>
  );
}

/**
 * A submit button that asks first, for changes that are hard to undo (they lock people out, stop sessions). The
 * question opens in a dialog; only "confirm" submits the button's form.
 */
export function ConfirmButton({ question, busy, children, variant = "secondary", size = "sm" }: {
  question: string;
  /** Its change is on the way (see Button). */
  busy?: boolean;
  children: ReactNode;
  variant?: ButtonVariant;
  size?: "sm" | "md";
}) {
  const t = useTranslations("Forms");
  const trigger = useRef<HTMLButtonElement>(null);
  const destructive = variant === "danger" || variant === "dangerOutline";
  return (
    <AlertDialog.Root>
      <AlertDialog.Trigger asChild>
        <Button ref={trigger} type="button" variant={variant} size={size} busy={busy}>{children}</Button>
      </AlertDialog.Trigger>
      <AlertDialog.Portal>
        <AlertDialog.Overlay className="fixed inset-0 z-50 bg-slate-900/40" />
        <AlertDialog.Content className="fixed top-1/2 left-1/2 z-50 w-[calc(100vw-2rem)] max-w-md -translate-x-1/2 -translate-y-1/2 rounded-lg bg-white p-6 shadow-xl focus:outline-none">
          <div className="flex gap-4">
            <div className={cn("flex size-10 shrink-0 items-center justify-center rounded-full", destructive ? "bg-red-100 text-red-600" : "bg-amber-100 text-amber-700")}>
              <TriangleAlert className="size-5" aria-hidden />
            </div>
            <div>
              <AlertDialog.Title className="text-base font-semibold text-slate-900">{t("confirmTitle")}</AlertDialog.Title>
              <AlertDialog.Description className="mt-1 text-sm text-slate-600">{question}</AlertDialog.Description>
            </div>
          </div>
          <div className="mt-6 flex justify-end gap-2">
            <AlertDialog.Cancel asChild>
              <Button type="button" variant="secondary">{t("cancel")}</Button>
            </AlertDialog.Cancel>
            {/* The dialog sits outside the form (in a portal), so the button submits the form it came from. */}
            <AlertDialog.Action asChild>
              <Button type="button" variant={destructive ? "danger" : "primary"} onClick={() => trigger.current?.form?.requestSubmit()}>
                {t("confirm")}
              </Button>
            </AlertDialog.Action>
          </div>
        </AlertDialog.Content>
      </AlertDialog.Portal>
    </AlertDialog.Root>
  );
}

/** A client secret or token, shown once: AuthService keeps only a secret's hash. */
export function OneTimeSecret({ label, secret, note }: { label: string; secret: string; note?: string }) {
  const t = useTranslations("Forms");
  const [copied, setCopied] = useState(false);

  async function copy() {
    await navigator.clipboard.writeText(secret);
    setCopied(true);
  }

  return (
    <div role="status" className="grid gap-2 rounded-md border border-amber-200 bg-amber-50 p-4">
      <p className="text-sm font-medium text-amber-900">{label}</p>
      <div className="flex items-start gap-2">
        <code data-secret className="block min-w-0 flex-1 rounded border border-amber-200 bg-white p-2 font-mono text-xs break-all select-all">
          {secret}
        </code>
        <Button type="button" variant="secondary" size="sm" onClick={copy}>
          <Copy aria-hidden />
          {copied ? t("copied") : t("copy")}
        </Button>
      </div>
      <p className="text-xs text-amber-800">{note ?? t("secretShownOnce")}</p>
    </div>
  );
}
