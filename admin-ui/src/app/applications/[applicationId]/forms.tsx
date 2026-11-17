"use client";

import { useActionState } from "react";
import { useTranslations } from "next-intl";
import { Monitor, ShieldAlert } from "lucide-react";
import { ConfirmButton, Refusal, Saved } from "@/components/form-parts";
import { Code } from "@/components/ui/badge";
import { Button } from "@/components/ui/button";
import { Field, Input, Textarea } from "@/components/ui/fields";
import { adminUiApplicationKey, canHaveDesktopSignIn, desktopClientId, type ApplicationDetails } from "@/lib/applications";
import { changeApplicationAction, setDesktopSignInAction } from "../actions";

/**
 * Desktop sign-in on or off. Off asks first: desktop users can't renew their tokens any more and must sign in
 * again once it's back on.
 */
export function DesktopSignInForm({ application }: { application: ApplicationDetails }) {
  const t = useTranslations("Application");
  const [state, action, pending] = useActionState(setDesktopSignInAction, undefined);
  if (!canHaveDesktopSignIn(application)) {
    return <p className="flex items-start gap-2 text-sm text-slate-500"><ShieldAlert className="mt-0.5 size-4 shrink-0" aria-hidden />{t("noDesktopForAdminUi")}</p>;
  }

  return (
    <form action={action} className="grid gap-3">
      <input type="hidden" name="id" value={application.id} />
      <input type="hidden" name="enabled" value={String(!application.desktopSignIn)} />
      {application.desktopSignIn && (
        <p className="text-sm">{t("desktopClientId")} <Code>{desktopClientId(application)}</Code></p>
      )}
      <div>
        {application.desktopSignIn
          ? (
            <ConfirmButton question={t("confirmDesktopOff", { key: application.key })} busy={pending} variant="dangerOutline" size="md">
              <Monitor aria-hidden />{t("desktopOff")}
            </ConfirmButton>
          )
          : <Button type="submit" variant="secondary" size="md" busy={pending}><Monitor aria-hidden />{t("desktopOn")}</Button>}
      </div>
      <Refusal state={state} />
    </form>
  );
}

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
        <Button type="submit" busy={pending}>{t("save")}</Button>
        <Saved state={state?.outcome} />
      </div>
    </form>
  );
}
