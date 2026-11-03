"use client";

import { useActionState } from "react";
import { useTranslations } from "next-intl";
import { Trash2 } from "lucide-react";
import { ConfirmButton, Refusal, Saved } from "@/components/form-parts";
import { PickDialog } from "@/components/pick-dialog";
import { Code } from "@/components/ui/badge";
import { Button } from "@/components/ui/button";
import { DataTable, type DataColumn } from "@/components/ui/data-table";
import { Field, Input } from "@/components/ui/fields";
import type { Choice } from "@/components/ui/multi-select";
import type { GroupDetails } from "@/lib/groups";
import type { User } from "@/lib/users";
import {
  addGroupRolesAction, addMembersAction, deleteGroupAction, removeGroupRoleAction, removeMemberAction, renameGroupAction,
} from "../actions";

type Member = GroupDetails["members"][number];
type GroupRole = GroupDetails["roles"][number];

export function RenameGroupForm({ group }: { group: GroupDetails }) {
  const t = useTranslations("Group");
  const [state, action, pending] = useActionState(renameGroupAction, undefined);
  const typed = state?.submitted;
  return (
    <form action={action} className="grid gap-4">
      <input type="hidden" name="groupId" value={group.id} />
      <Field label={t("name")}>
        <Input name="name" required defaultValue={typed?.name ?? group.name} />
      </Field>
      <Field label={t("description")}>
        <Input name="description" defaultValue={(typed ? typed.description : group.description) ?? ""} />
      </Field>
      <Refusal state={state?.outcome} />
      <div className="flex items-center gap-3">
        <Button type="submit" disabled={pending}>{t("rename")}</Button>
        <Saved state={state?.outcome} />
      </div>
    </form>
  );
}

export function DeleteGroupButton({ group }: { group: GroupDetails }) {
  const t = useTranslations("Group");
  const [state, action, pending] = useActionState(deleteGroupAction, undefined);
  return (
    <form action={action} className="grid justify-items-end gap-2">
      <input type="hidden" name="groupId" value={group.id} />
      <ConfirmButton question={t("confirmDelete", { name: group.name })} disabled={pending} variant="dangerOutline" size="md">
        <Trash2 aria-hidden />{t("delete")}
      </ConfirmButton>
      <Refusal state={state} />
    </form>
  );
}

export function MembersTable({ groupId, members }: { groupId: string; members: Member[] }) {
  const t = useTranslations("Group");
  const columns: DataColumn<Member>[] = [
    {
      accessorKey: "userName",
      header: t("user"),
      cell: ({ row }) => <a href={`/users/${encodeURIComponent(row.original.userId)}`} className="font-medium">{row.original.userName}</a>,
    },
    { accessorKey: "displayName", header: t("displayName") },
    {
      id: "actions",
      enableSorting: false,
      header: () => <span className="sr-only">{t("actions")}</span>,
      cell: ({ row }) => <RemoveMemberButton groupId={groupId} member={row.original} />,
    },
  ];

  return <DataTable columns={columns} data={members} empty={t("noMembers")} initialSorting={[{ id: "userName", desc: false }]} />;
}

function RemoveMemberButton({ groupId, member }: { groupId: string; member: Member }) {
  const t = useTranslations("Group");
  const [state, action, pending] = useActionState(removeMemberAction, undefined);
  return (
    <form action={action} className="grid justify-items-end gap-1">
      <input type="hidden" name="groupId" value={groupId} />
      <input type="hidden" name="userId" value={member.userId} />
      <ConfirmButton question={t("confirmRemoveMember", { userName: member.userName })} disabled={pending} variant="ghost">
        <Trash2 aria-hidden />{t("remove")}
      </ConfirmButton>
      <Refusal state={state} />
    </form>
  );
}

export function GroupRolesTable({ groupId, roles }: { groupId: string; roles: GroupRole[] }) {
  const t = useTranslations("Group");
  const columns: DataColumn<GroupRole>[] = [
    { accessorKey: "applicationKey", header: t("application"), cell: ({ row }) => <Code>{row.original.applicationKey}</Code> },
    { accessorKey: "name", header: t("role") },
    {
      id: "actions",
      enableSorting: false,
      header: () => <span className="sr-only">{t("actions")}</span>,
      cell: ({ row }) => <RemoveGroupRoleButton groupId={groupId} role={row.original} />,
    },
  ];

  return <DataTable columns={columns} data={roles} empty={t("noRoles")} initialSorting={[{ id: "applicationKey", desc: false }]} />;
}

function RemoveGroupRoleButton({ groupId, role }: { groupId: string; role: GroupRole }) {
  const t = useTranslations("Group");
  const [state, action, pending] = useActionState(removeGroupRoleAction, undefined);
  return (
    <form action={action} className="grid justify-items-end gap-1">
      <input type="hidden" name="groupId" value={groupId} />
      <input type="hidden" name="roleId" value={role.roleId} />
      <ConfirmButton question={t("confirmRemoveRole", { role: role.name, key: role.applicationKey })} disabled={pending} variant="ghost">
        <Trash2 aria-hidden />{t("remove")}
      </ConfirmButton>
      <Refusal state={state} />
    </form>
  );
}

/** The users who aren't members yet; a disabled one says so. */
export function AddMembersDialog({ groupId, users }: { groupId: string; users: User[] }) {
  const t = useTranslations("Group");
  const choices: Choice[] = users.map(user => ({
    value: user.id,
    label: user.userName,
    detail: user.isActive ? user.displayName : `${user.displayName} – ${t("disabledUser")}`,
  }));
  return (
    <PickDialog triggerLabel={t("addMembers")} title={t("addMembersTitle")} description={t("addMembersExplanation")}
      action={addMembersAction} hidden={{ groupId }} name="userId" label={t("users")} placeholder={t("pickUsers")} choices={choices}
      submitLabel={count => t("addMembersPicked", { count })} nothingLeft={t("noUsersLeft")} />
  );
}

/** The roles the group doesn't hold yet, listed under their application. */
export function AddGroupRolesDialog({ groupId, choices }: { groupId: string; choices: Choice[] }) {
  const t = useTranslations("Group");
  return (
    <PickDialog triggerLabel={t("addRoles")} title={t("addRolesTitle")} description={t("addRolesExplanation")}
      action={addGroupRolesAction} hidden={{ groupId }} name="roleId" label={t("roles")} placeholder={t("pickRoles")} choices={choices}
      submitLabel={count => t("addRolesPicked", { count })} nothingLeft={t("noRolesLeft")} />
  );
}
