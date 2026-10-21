"use client";

import type { MouseEvent, ReactNode } from "react";
import { useTranslations } from "next-intl";
import { refusalMessage, type FormState } from "@/lib/admin-requests";

/** Why a change didn't happen (nothing before the first submit, or after a success). */
export function Refusal({ state }: { state: FormState<unknown> }) {
  const t = useTranslations("Forms");
  const message = refusalMessage(state, t);
  return message ? <p role="alert" className="refusal">{message}</p> : null;
}

/** A submit button that asks first, for changes that are hard to undo (they lock people out, stop sessions). */
export function ConfirmButton({ question, disabled, children }: { question: string; disabled?: boolean; children: ReactNode }) {
  function askFirst(event: MouseEvent<HTMLButtonElement>) {
    if (!window.confirm(question)) {
      event.preventDefault();
    }
  }

  return <button type="submit" onClick={askFirst} disabled={disabled}>{children}</button>;
}

/** A client secret, shown once: AuthService keeps only its hash. */
export function OneTimeSecret({ label, secret }: { label: string; secret: string }) {
  const t = useTranslations("Forms");
  return (
    <div role="status" className="secret">
      <p>{label}</p>
      <code>{secret}</code>
      <p>{t("secretShownOnce")}</p>
    </div>
  );
}
