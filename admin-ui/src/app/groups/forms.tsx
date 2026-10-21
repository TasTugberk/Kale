"use client";

import { useActionState } from "react";
import { useTranslations } from "next-intl";
import { Refusal } from "@/components/form-parts";
import { createGroupAction } from "./actions";

export function NewGroupForm() {
  const t = useTranslations("Groups");
  const [state, action, pending] = useActionState(createGroupAction, undefined);
  const typed = state?.submitted;
  const created = state?.outcome.ok ? state.outcome.value : undefined;
  return (
    <form action={action} className="stacked">
      <label>{t("name")} <input name="name" required defaultValue={typed?.name} /></label>
      <label>{t("description")} <input name="description" defaultValue={typed?.description ?? ""} /></label>
      <button type="submit" disabled={pending}>{t("create")}</button>
      <Refusal state={state?.outcome} />
      {created && (
        <p role="status">
          {t("created")} <a href={`/groups/${encodeURIComponent(created.id)}`}>{created.name}</a>
        </p>
      )}
    </form>
  );
}
