"use client";

import { useActionState } from "react";
import { useTranslations } from "next-intl";
import { Refusal, Saved } from "@/components/form-parts";
import type { ApplicationDetails } from "@/lib/applications";
import { changeApplicationAction } from "../actions";

/** Name and return addresses (one per line). After a refusal the fields keep what was sent. */
export function EditApplicationForm({ application }: { application: ApplicationDetails }) {
  const t = useTranslations("Application");
  const [state, action, pending] = useActionState(changeApplicationAction, undefined);
  const shown = state?.submitted ?? application;
  return (
    <form action={action} className="stacked">
      <input type="hidden" name="id" value={application.id} />
      <label>{t("name")} <input name="name" required defaultValue={shown.name} /></label>
      <label>{t("redirectUris")} <textarea name="redirectUris" rows={3} defaultValue={shown.redirectUris.join("\n")} /></label>
      <label>{t("postLogoutRedirectUris")} <textarea name="postLogoutRedirectUris" rows={3} defaultValue={shown.postLogoutRedirectUris.join("\n")} /></label>
      <p className="hint">{t("onePerLine")}</p>
      <button type="submit" disabled={pending}>{t("save")}</button>
      <Refusal state={state?.outcome} />
      <Saved state={state?.outcome} />
    </form>
  );
}
