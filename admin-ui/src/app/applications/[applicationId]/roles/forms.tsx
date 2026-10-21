"use client";

import { useActionState } from "react";
import { useTranslations } from "next-intl";
import { ConfirmButton, Refusal, Saved } from "@/components/form-parts";
import { obsoleteGrants, type Operation, type Role } from "@/lib/roles";
import { createRoleAction, deleteRoleAction, renameRoleAction, setOperationsAction } from "./actions";

export function NewRoleForm({ applicationId }: { applicationId: string }) {
  const t = useTranslations("Roles");
  const [state, action, pending] = useActionState(createRoleAction, undefined);
  const typed = state?.submitted;
  return (
    <form action={action} className="stacked">
      <input type="hidden" name="applicationId" value={applicationId} />
      <label>{t("name")} <input name="name" required defaultValue={typed?.name} /></label>
      <label>{t("description")} <input name="description" defaultValue={typed?.description ?? ""} /></label>
      <button type="submit" disabled={pending}>{t("create")}</button>
      <Refusal state={state?.outcome} />
    </form>
  );
}

/** One role: its operations (check boxes), its name and description, and deleting it. */
export function RoleForms({ role, operations }: { role: Role; operations: Operation[] }) {
  const t = useTranslations("Roles");
  const [granted, grant, granting] = useActionState(setOperationsAction, undefined);
  const [renamed, rename, renaming] = useActionState(renameRoleAction, undefined);
  const [deleted, remove, removing] = useActionState(deleteRoleAction, undefined);

  // After a refusal, the boxes show what was sent; otherwise what the role holds.
  const ticked = granted?.submitted ?? role.operations;
  const grantable = operations.filter(operation => !operation.isObsolete);
  const dropped = obsoleteGrants(role.operations, operations);
  const typed = renamed?.submitted;
  return (
    <>
      <form action={grant}>
        <input type="hidden" name="roleId" value={role.id} />
        {grantable.length === 0 && <p className="hint">{t("noOperations")}</p>}
        {grantable.map(operation => (
          <label key={operation.id} className="choice">
            <input type="checkbox" name="operation" value={operation.name} defaultChecked={ticked.includes(operation.name)} />
            <code>{operation.name}</code>
          </label>
        ))}
        {dropped.length > 0 && <p className="hint">{t("obsoleteGrants", { operations: dropped.join(", ") })}</p>}
        <button type="submit" disabled={granting}>{t("saveOperations")}</button>
        <Refusal state={granted?.outcome} />
        <Saved state={granted?.outcome} />
      </form>
      <form action={rename} className="inline">
        <input type="hidden" name="roleId" value={role.id} />
        <input name="name" required aria-label={t("name")} defaultValue={typed?.name ?? role.name} />
        <input name="description" aria-label={t("description")} placeholder={t("description")}
          defaultValue={(typed ? typed.description : role.description) ?? ""} />
        <button type="submit" disabled={renaming}>{t("rename")}</button>
      </form>
      <form action={remove} className="inline">
        <input type="hidden" name="roleId" value={role.id} />
        <ConfirmButton question={t("confirmDelete", { name: role.name })} disabled={removing}>{t("delete")}</ConfirmButton>
      </form>
      <Refusal state={renamed?.outcome} />
      <Refusal state={deleted} />
    </>
  );
}
