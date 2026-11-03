"use client";

import { useActionState, useState } from "react";
import { useTranslations } from "next-intl";
import { Plus } from "lucide-react";
import { Refusal } from "@/components/form-parts";
import { Badge, Code } from "@/components/ui/badge";
import { Button } from "@/components/ui/button";
import { DataTable, type DataColumn } from "@/components/ui/data-table";
import { Dialog, DialogActions } from "@/components/ui/dialog";
import { Field, Input } from "@/components/ui/fields";
import type { SubmitState } from "@/lib/admin-requests";
import type { Operation, Role, RoleFields } from "@/lib/roles";
import { createRoleAction } from "./actions";

export function RolesTable({ applicationId, roles }: { applicationId: string; roles: Role[] }) {
  const t = useTranslations("Roles");
  const columns: DataColumn<Role>[] = [
    {
      accessorKey: "name",
      header: t("name"),
      cell: ({ row }) => (
        <a href={`/applications/${encodeURIComponent(applicationId)}/roles/${encodeURIComponent(row.original.id)}`} className="font-medium">
          {row.original.name}
        </a>
      ),
    },
    { id: "description", accessorFn: role => role.description ?? "", header: t("description"), cell: ({ row }) => <span className="text-slate-500">{row.original.description}</span> },
    {
      id: "operations",
      accessorFn: role => role.operations.length,
      header: t("operationCount"),
      cell: ({ row }) => <Badge tone={row.original.operations.length > 0 ? "blue" : "grey"}>{row.original.operations.length}</Badge>,
    },
  ];

  return <DataTable columns={columns} data={roles} empty={t("noRoles")} initialSorting={[{ id: "name", desc: false }]} />;
}

export function OperationsTable({ operations }: { operations: Operation[] }) {
  const t = useTranslations("Roles");
  const columns: DataColumn<Operation>[] = [
    { accessorKey: "name", header: t("operation"), cell: ({ row }) => <Code>{row.original.name}</Code> },
    {
      id: "status",
      accessorFn: operation => operation.isObsolete ? t("obsolete") : t("current"),
      header: t("status"),
      cell: ({ row }) => row.original.isObsolete
        ? <Badge tone="amber" className="whitespace-normal">{t("obsolete")}</Badge>
        : <Badge tone="green">{t("current")}</Badge>,
    },
  ];

  return <DataTable columns={columns} data={operations} empty={t("noOperations")} initialSorting={[{ id: "name", desc: false }]} />;
}

export function NewRoleDialog({ applicationId }: { applicationId: string }) {
  const t = useTranslations("Roles");
  const tf = useTranslations("Forms");
  const [open, setOpen] = useState(false);
  const [state, action, pending] = useActionState(async (previous: SubmitState<undefined, RoleFields> | undefined, form: FormData) => {
    const result = await createRoleAction(previous, form);
    // Made: the dialog closes and the role shows in the list. Refused: it stays open with the reason.
    if (result.outcome.ok) {
      setOpen(false);
    }

    return result;
  }, undefined);
  const typed = state?.submitted;
  return (
    <Dialog open={open} onOpenChange={setOpen} title={t("new")} trigger={<Button><Plus aria-hidden />{t("new")}</Button>}>
      <form action={action} className="grid gap-4">
        <input type="hidden" name="applicationId" value={applicationId} />
        <Field label={t("name")}>
          <Input name="name" required defaultValue={typed?.name} />
        </Field>
        <Field label={t("description")}>
          <Input name="description" defaultValue={typed?.description ?? ""} />
        </Field>
        <Refusal state={state?.outcome} />
        <DialogActions>
          <Button type="button" variant="secondary" onClick={() => setOpen(false)}>{tf("cancel")}</Button>
          <Button type="submit" busy={pending}>{t("create")}</Button>
        </DialogActions>
      </form>
    </Dialog>
  );
}
