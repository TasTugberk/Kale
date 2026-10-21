"use client";

import { useActionState } from "react";
import { useTranslations } from "next-intl";
import { ConfirmButton, OneTimeSecret, Refusal } from "@/components/form-parts";
import { canRotateSecret, type Application } from "@/lib/applications";
import { createApplicationAction, rotateSecretAction, setActiveAction } from "./actions";

export function NewApplicationForm() {
  const t = useTranslations("Applications");
  const [state, action, pending] = useActionState(createApplicationAction, undefined);
  const outcome = state?.outcome;
  // React empties a form after its action runs; after a refusal, the fields start from what was sent instead.
  const typed = state?.submitted;
  return (
    <form action={action} className="stacked">
      <label>{t("key")} <input name="key" required defaultValue={typed?.key} /></label>
      <p className="hint">{t("keyHint")}</p>
      <label>{t("name")} <input name="name" required defaultValue={typed?.name} /></label>
      <label>
        {t("redirectUris")}
        <textarea name="redirectUris" rows={2} required placeholder="https://app.example/signin-oidc" defaultValue={typed?.redirectUris.join("\n")} />
      </label>
      <label>
        {t("postLogoutRedirectUris")}
        <textarea name="postLogoutRedirectUris" rows={2} defaultValue={typed?.postLogoutRedirectUris.join("\n")} />
      </label>
      <p className="hint">{t("onePerLine")}</p>
      <button type="submit" disabled={pending}>{t("create")}</button>
      <Refusal state={outcome} />
      {outcome?.ok && <OneTimeSecret label={t("createdSecret", { key: outcome.value.key })} secret={outcome.value.clientSecret} />}
    </form>
  );
}

/** A row's changes: a new client secret, and switching the application on or off. */
export function ApplicationActions({ application }: { application: Application }) {
  const t = useTranslations("Applications");
  const [secret, rotate, rotating] = useActionState(rotateSecretAction, undefined);
  const [active, setActive, switching] = useActionState(setActiveAction, undefined);
  return (
    <>
      {canRotateSecret(application)
        ? (
          <form action={rotate} className="inline">
            <input type="hidden" name="id" value={application.id} />
            <ConfirmButton question={t("confirmRotate", { key: application.key })} disabled={rotating}>{t("rotate")}</ConfirmButton>
          </form>
        )
        : <span className="hint">{t("adminUiSecret")} </span>}
      <form action={setActive} className="inline">
        <input type="hidden" name="id" value={application.id} />
        <input type="hidden" name="isActive" value={String(!application.isActive)} />
        {application.isActive
          ? <ConfirmButton question={t("confirmDeactivate", { key: application.key })} disabled={switching}>{t("deactivate")}</ConfirmButton>
          : <button type="submit" disabled={switching}>{t("activate")}</button>}
      </form>
      <Refusal state={secret} />
      <Refusal state={active} />
      {secret?.ok && <OneTimeSecret label={t("rotatedSecret", { key: application.key })} secret={secret.value} />}
    </>
  );
}
