"use client";

import { useActionState } from "react";
import { useTranslations } from "next-intl";
import { ConfirmButton, Refusal } from "@/components/form-parts";
import type { OpenSession } from "@/lib/open-sessions";
import { stopSessionAction } from "./actions";

export function StopSessionForm({ session }: { session: OpenSession }) {
  const t = useTranslations("Sessions");
  const [state, action, pending] = useActionState(stopSessionAction, undefined);
  return (
    <form action={action} className="inline">
      <input type="hidden" name="sessionId" value={session.id} />
      <ConfirmButton question={t("confirmStop", { userName: session.userName, key: session.applicationKey })} disabled={pending}>
        {t("stop")}
      </ConfirmButton>
      <Refusal state={state} />
    </form>
  );
}
