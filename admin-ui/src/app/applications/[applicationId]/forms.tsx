"use client";

import { useActionState } from "react";
import { useTranslations } from "next-intl";
import { Refusal, Saved } from "@/components/form-parts";
import { Button } from "@/components/ui/button";
import { Field, Input, Textarea } from "@/components/ui/fields";
import { adminUiApplicationKey, type ApplicationDetails } from "@/lib/applications";
import { changeApplicationAction } from "../actions";

/** Name and return addresses (one per line). After a refusal the fields keep what was sent. */
export function EditApplicationForm({ application }: { application: ApplicationDetails }) {
  const t = useTranslations("Application");
  const [state, action, pending] = useActionState(changeApplicationAction, undefined);
  const shown = state?.submitted ?? application;
  // The admin UI's own addresses can't change (AuthService refuses); shown, but not editable.
  const fixedAddresses = application.key === adminUiApplicationKey;
  return (
    <form action={action} className="grid gap-4">
      <input type="hidden" name="id" value={application.id} />
      <Field label={t("name")}>
        <Input name="name" required defaultValue={shown.name} />
      </Field>
      <Field label={t("redirectUris")} hint={t("onePerLine")}>
        <Textarea name="redirectUris" rows={3} readOnly={fixedAddresses} defaultValue={shown.redirectUris.join("\n")} />
      </Field>
      <Field label={t("postLogoutRedirectUris")} hint={t("onePerLine")}>
        <Textarea name="postLogoutRedirectUris" rows={3} readOnly={fixedAddresses} defaultValue={shown.postLogoutRedirectUris.join("\n")} />
      </Field>
      <Refusal state={state?.outcome} />
      <div className="flex items-center gap-3">
        <Button type="submit" disabled={pending}>{t("save")}</Button>
        <Saved state={state?.outcome} />
      </div>
    </form>
  );
}
