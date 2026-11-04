"use client";

import { useActionState } from "react";
import { useTranslations } from "next-intl";
import { Trash2 } from "lucide-react";
import { ConfirmButton, Refusal, Saved } from "@/components/form-parts";
import { PickDialog } from "@/components/pick-dialog";
import { Badge, Code } from "@/components/ui/badge";
import { Button } from "@/components/ui/button";
import { DataTable, type DataColumn } from "@/components/ui/data-table";
import { Field, Input } from "@/components/ui/fields";
import { obsoleteDroppedWith, type Operation, type Role } from "@/lib/roles";
import { addOperationsAction, deleteRoleAction, removeOperationAction, renameRoleAction } from "../actions";

interface Grant {
  name: string;
  isObsolete: boolean;
}

/** The operations the role grants directly (the ones they imply aren't listed), each with "remove". */
export function RoleOperationsTable({ applicationId, role, obsolete }: { applicationId: string; role: Role; obsolete: string[] }) {
  const t = useTranslations("Role");
  const grants: Grant[] = role.operations.map(name => ({ name, isObsolete: obsolete.includes(name) }));
  const columns: DataColumn<Grant>[] = [
    { accessorKey: "name", header: t("operation"), cell: ({ row }) => <Code>{row.original.name}</Code> },
    {
      id: "status",
      accessorFn: grant => grant.isObsolete ? t("obsolete") : "",
      header: t("status"),
      cell: ({ row }) => row.original.isObsolete && <Badge tone="amber" className="whitespace-normal">{t("obsolete")}</Badge>,
    },
    {
      id: "actions",
      enableSorting: false,
      header: () => <span className="sr-only">{t("actions")}</span>,
      cell: ({ row }) => <RemoveOperationButton applicationId={applicationId} role={role} operation={row.original.name} obsolete={obsolete} />,
    },
  ];

  return <DataTable columns={columns} data={grants} rowId={grant => grant.name} empty={t("noOperations")} initialSorting={[{ id: "name", desc: false }]} />;
}

function RemoveOperationButton({ applicationId, role, operation, obsolete }: {
  applicationId: string; role: Role; operation: string; obsolete: string[];
}) {
  const t = useTranslations("Role");
  const [state, action, pending] = useActionState(removeOperationAction, undefined);
  // Saving drops the role's obsolete grants too (AuthService refuses to save them), so the question says so.
  const dropped = obsoleteDroppedWith(operation, obsolete);
  const question = dropped.length > 0
    ? `${t("confirmRemove", { operation, name: role.name })} ${t("obsoleteDropped", { operations: dropped.join(", ") })}`
    : t("confirmRemove", { operation, name: role.name });
  return (
    <form action={action} className="grid justify-items-end gap-1">
      <input type="hidden" name="applicationId" value={applicationId} />
      <input type="hidden" name="roleId" value={role.id} />
      <input type="hidden" name="operation" value={operation} />
      <ConfirmButton question={question} busy={pending} variant="ghost">
        <Trash2 aria-hidden />{t("remove")}
      </ConfirmButton>
      <Refusal state={state} />
    </form>
  );
}

/** Picks operations the role doesn't hold yet; they're granted on top of what it holds. */
export function AddOperationsDialog({ applicationId, role, grantable, obsolete }: {
  applicationId: string; role: Role; grantable: Operation[]; obsolete: string[];
}) {
  const t = useTranslations("Role");
  // Saving drops obsolete grants (AuthService refuses to save them), so the dialog says so first.
  const note = obsolete.length > 0
    ? <p className="rounded-md bg-amber-50 px-3 py-2 text-sm text-amber-800">{t("obsoleteDropped", { operations: obsolete.join(", ") })}</p>
    : undefined;
  return (
    <PickDialog triggerLabel={t("add")} title={t("addTitle", { name: role.name })} description={t("addExplanation")}
      action={addOperationsAction} hidden={{ applicationId, roleId: role.id }} name="operation" label={t("operations")}
      placeholder={t("pick")} choices={grantable.map(operation => ({ value: operation.name, label: operation.name }))}
      submitLabel={count => t("addPicked", { count })} nothingLeft={t("nothingToAdd")} note={note} />
  );
}

export function RenameRoleForm({ role }: { role: Role }) {
  const t = useTranslations("Role");
  const [state, action, pending] = useActionState(renameRoleAction, undefined);
  const typed = state?.submitted;
  return (
    <form action={action} className="grid gap-4">
      <input type="hidden" name="roleId" value={role.id} />
      <Field label={t("name")}>
        <Input name="name" required defaultValue={typed?.name ?? role.name} />
      </Field>
      <Field label={t("description")}>
        <Input name="description" defaultValue={(typed ? typed.description : role.description) ?? ""} />
      </Field>
      <Refusal state={state?.outcome} />
      <div className="flex items-center gap-3">
        <Button type="submit" busy={pending}>{t("save")}</Button>
        <Saved state={state?.outcome} />
      </div>
    </form>
  );
}

export function DeleteRoleButton({ applicationId, role }: { applicationId: string; role: Role }) {
  const t = useTranslations("Role");
  const [state, action, pending] = useActionState(deleteRoleAction, undefined);
  return (
    <form action={action} className="grid justify-items-end gap-2">
      <input type="hidden" name="applicationId" value={applicationId} />
      <input type="hidden" name="roleId" value={role.id} />
      <ConfirmButton question={t("confirmDelete", { name: role.name })} busy={pending} variant="dangerOutline" size="md">
        <Trash2 aria-hidden />{t("delete")}
      </ConfirmButton>
      <Refusal state={state} />
    </form>
  );
}
