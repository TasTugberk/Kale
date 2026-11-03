"use client";

import { useActionState, useState } from "react";
import { useTranslations } from "next-intl";
import { Plus, Power, RefreshCw, ShieldAlert } from "lucide-react";
import { ConfirmButton, OneTimeSecret, Refusal } from "@/components/form-parts";
import { Badge, Code } from "@/components/ui/badge";
import { Button, ButtonLink } from "@/components/ui/button";
import { DataTable, type DataColumn } from "@/components/ui/data-table";
import { Dialog, DialogActions } from "@/components/ui/dialog";
import { Field, Input, Textarea } from "@/components/ui/fields";
import type { FormState, SubmitState } from "@/lib/admin-requests";
import { canRotateSecret, type Application, type CreatedApplication, type NewApplication } from "@/lib/applications";
import { createApplicationAction, rotateSecretAction, setActiveAction } from "./actions";

export function ApplicationsTable({ applications }: { applications: Application[] }) {
  const t = useTranslations("Applications");
  const columns: DataColumn<Application>[] = [
    {
      accessorKey: "key",
      header: t("key"),
      cell: ({ row }) => <a href={`/applications/${encodeURIComponent(row.original.id)}`}><Code>{row.original.key}</Code></a>,
    },
    { accessorKey: "name", header: t("name") },
    {
      id: "status",
      // Text, so the search finds "active"/"inactive" too.
      accessorFn: application => application.isActive ? t("active") : t("inactive"),
      header: t("status"),
      cell: ({ row }) => <StatusBadge isActive={row.original.isActive} />,
    },
    {
      id: "actions",
      enableSorting: false,
      header: () => <span className="sr-only">{t("actions")}</span>,
      cell: ({ row }) => (
        <div className="flex flex-wrap items-center justify-end gap-2">
          <ButtonLink href={`/applications/${encodeURIComponent(row.original.id)}/roles`}>{t("roles")}</ButtonLink>
          <ActiveButton application={row.original} />
        </div>
      ),
    },
  ];

  return <DataTable columns={columns} data={applications} empty={t("none")} initialSorting={[{ id: "key", desc: false }]} />;
}

export function StatusBadge({ isActive }: { isActive: boolean }) {
  const t = useTranslations("Applications");
  return isActive ? <Badge tone="green">{t("active")}</Badge> : <Badge tone="grey">{t("inactive")}</Badge>;
}

export function NewApplicationDialog() {
  const t = useTranslations("Applications");
  const tf = useTranslations("Forms");
  const [open, setOpen] = useState(false);
  // The new secret, shown in place of the form until the dialog closes: AuthService keeps only its hash.
  const [created, setCreated] = useState<CreatedApplication>();
  const [state, action, pending] = useActionState(
    async (previous: SubmitState<CreatedApplication, NewApplication> | undefined, form: FormData) => {
      const result = await createApplicationAction(previous, form);
      if (result.outcome.ok) {
        setCreated(result.outcome.value);
      }

      return result;
    },
    undefined,
  );

  function openChange(next: boolean) {
    setOpen(next);
    if (!next) {
      setCreated(undefined);
    }
  }

  // React empties a form after its action runs; after a refusal, the fields start from what was sent instead.
  const typed = state?.submitted;
  return (
    <Dialog open={open} onOpenChange={openChange} title={t("new")} description={t("newExplanation")} wide
      trigger={<Button><Plus aria-hidden />{t("new")}</Button>}>
      {created
        ? (
          <>
            <OneTimeSecret label={t("createdSecret", { key: created.key })} secret={created.clientSecret} />
            <DialogActions><Button type="button" onClick={() => openChange(false)}>{tf("done")}</Button></DialogActions>
          </>
        )
        : (
          <form action={action} className="grid gap-4">
            <Field label={t("key")} hint={t("keyHint")}>
              <Input name="key" required autoComplete="off" defaultValue={typed?.key} className="font-mono" />
            </Field>
            <Field label={t("name")}>
              <Input name="name" required defaultValue={typed?.name} />
            </Field>
            <Field label={t("redirectUris")} hint={t("onePerLine")}>
              <Textarea name="redirectUris" rows={2} placeholder="https://app.example/signin-oidc" defaultValue={typed?.redirectUris.join("\n")} />
            </Field>
            <Field label={t("postLogoutRedirectUris")} hint={t("onePerLine")}>
              <Textarea name="postLogoutRedirectUris" rows={2} defaultValue={typed?.postLogoutRedirectUris.join("\n")} />
            </Field>
            <Refusal state={state?.outcome} />
            <DialogActions>
              <Button type="button" variant="secondary" onClick={() => openChange(false)}>{tf("cancel")}</Button>
              <Button type="submit" disabled={pending}>{t("create")}</Button>
            </DialogActions>
          </form>
        )}
    </Dialog>
  );
}

/** A new client secret, shown once in a dialog. Not for "auth": that secret is the admin UI's own. */
export function RotateSecretButton({ application }: { application: Application }) {
  const t = useTranslations("Applications");
  const tf = useTranslations("Forms");
  const [secret, setSecret] = useState<string>();
  const [state, rotate, rotating] = useActionState(async (previous: FormState<string>, form: FormData) => {
    const outcome = await rotateSecretAction(previous, form);
    if (outcome?.ok) {
      setSecret(outcome.value);
    }

    return outcome;
  }, undefined);

  if (!canRotateSecret(application)) {
    return <p className="flex items-start gap-2 text-sm text-slate-500"><ShieldAlert className="mt-0.5 size-4 shrink-0" aria-hidden />{t("adminUiSecret")}</p>;
  }

  return (
    <div className="grid gap-2">
      <form action={rotate}>
        <input type="hidden" name="id" value={application.id} />
        <ConfirmButton question={t("confirmRotate", { key: application.key })} disabled={rotating} variant="dangerOutline" size="md">
          <RefreshCw aria-hidden />{t("rotate")}
        </ConfirmButton>
      </form>
      <Refusal state={state} />
      <Dialog open={secret !== undefined} onOpenChange={open => { if (!open) setSecret(undefined); }} title={t("rotatedTitle")}>
        {secret && <OneTimeSecret label={t("rotatedSecret", { key: application.key })} secret={secret} />}
        <DialogActions><Button type="button" onClick={() => setSecret(undefined)}>{tf("done")}</Button></DialogActions>
      </Dialog>
    </div>
  );
}

/** Switches the application on or off; switching it off asks first. */
export function ActiveButton({ application, size = "sm" }: { application: Application; size?: "sm" | "md" }) {
  const t = useTranslations("Applications");
  const [state, action, pending] = useActionState(setActiveAction, undefined);
  return (
    <form action={action} className="inline-grid gap-1">
      <input type="hidden" name="id" value={application.id} />
      <input type="hidden" name="isActive" value={String(!application.isActive)} />
      {application.isActive
        ? (
          <ConfirmButton question={t("confirmDeactivate", { key: application.key })} disabled={pending} variant="dangerOutline" size={size}>
            <Power aria-hidden />{t("deactivate")}
          </ConfirmButton>
        )
        : <Button type="submit" variant="secondary" size={size} disabled={pending}><Power aria-hidden />{t("activate")}</Button>}
      <Refusal state={state} />
    </form>
  );
}
