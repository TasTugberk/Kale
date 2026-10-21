"use client";

import { useActionState } from "react";
import { useTranslations } from "next-intl";
import { Refusal } from "@/components/form-parts";
import { createUserAction } from "./actions";

export function NewUserForm() {
  const t = useTranslations("Users");
  const [state, action, pending] = useActionState(createUserAction, undefined);
  const typed = state?.submitted;
  const created = state?.outcome.ok ? state.outcome.value : undefined;
  return (
    <form action={action} className="stacked">
      <label>{t("userName")} <input name="userName" required autoComplete="off" defaultValue={typed?.userName} /></label>
      <label>{t("displayName")} <input name="displayName" required defaultValue={typed?.displayName} /></label>
      {/* Never filled in again after a refusal: the password isn't sent back to the browser. */}
      <label>{t("password")} <input name="password" type="password" required autoComplete="new-password" /></label>
      <p className="hint">{t("passwordHint")}</p>
      <button type="submit" disabled={pending}>{t("create")}</button>
      <Refusal state={state?.outcome} />
      {created && (
        <p role="status">
          {t("created")} <a href={`/users/${encodeURIComponent(created.id)}`}>{created.userName}</a>
        </p>
      )}
    </form>
  );
}
