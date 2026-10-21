"use client";

import { useActionState } from "react";
import { useTranslations } from "next-intl";
import { ConfirmButton, Refusal } from "@/components/form-parts";
import { RoleSelect } from "@/components/role-select";
import type { GroupDetails } from "@/lib/groups";
import type { ApplicationRoles } from "@/lib/roles";
import type { User } from "@/lib/users";
import {
  addGroupRoleAction, addMemberAction, deleteGroupAction, removeGroupRoleAction, removeMemberAction, renameGroupAction,
} from "../actions";

export function RenameGroupForm({ group }: { group: GroupDetails }) {
  const t = useTranslations("Group");
  const [state, action, pending] = useActionState(renameGroupAction, undefined);
  const typed = state?.submitted;
  return (
    <form action={action} className="inline">
      <input type="hidden" name="groupId" value={group.id} />
      <input name="name" required aria-label={t("name")} defaultValue={typed?.name ?? group.name} />{" "}
      <input name="description" aria-label={t("description")} placeholder={t("description")}
        defaultValue={(typed ? typed.description : group.description) ?? ""} />{" "}
      <button type="submit" disabled={pending}>{t("rename")}</button>
      <Refusal state={state?.outcome} />
    </form>
  );
}

export function DeleteGroupForm({ group }: { group: GroupDetails }) {
  const t = useTranslations("Group");
  const [state, action, pending] = useActionState(deleteGroupAction, undefined);
  return (
    <form action={action} className="inline">
      <input type="hidden" name="groupId" value={group.id} />
      <ConfirmButton question={t("confirmDelete", { name: group.name })} disabled={pending}>{t("delete")}</ConfirmButton>
      <Refusal state={state} />
    </form>
  );
}

export function RemoveMemberForm({ groupId, member }: { groupId: string; member: GroupDetails["members"][number] }) {
  const t = useTranslations("Group");
  const [state, action, pending] = useActionState(removeMemberAction, undefined);
  return (
    <form action={action} className="inline">
      <input type="hidden" name="groupId" value={groupId} />
      <input type="hidden" name="userId" value={member.userId} />
      <ConfirmButton question={t("confirmRemoveMember", { userName: member.userName })} disabled={pending}>{t("remove")}</ConfirmButton>
      <Refusal state={state} />
    </form>
  );
}

/** The users who aren't members yet. */
export function AddMemberForm({ groupId, users }: { groupId: string; users: User[] }) {
  const t = useTranslations("Group");
  const [state, action, pending] = useActionState(addMemberAction, undefined);
  if (users.length === 0) {
    return null;
  }

  return (
    <form action={action}>
      <input type="hidden" name="groupId" value={groupId} />
      <select name="userId" aria-label={t("user")} required>
        {users.map(user => (
          <option key={user.id} value={user.id}>
            {user.userName} ({user.displayName}){user.isActive ? "" : ` – ${t("disabledUser")}`}
          </option>
        ))}
      </select>{" "}
      <button type="submit" disabled={pending}>{t("addMember")}</button>
      <Refusal state={state} />
    </form>
  );
}

export function RemoveGroupRoleForm({ groupId, role }: { groupId: string; role: GroupDetails["roles"][number] }) {
  const t = useTranslations("Group");
  const [state, action, pending] = useActionState(removeGroupRoleAction, undefined);
  return (
    <form action={action} className="inline">
      <input type="hidden" name="groupId" value={groupId} />
      <input type="hidden" name="roleId" value={role.roleId} />
      <ConfirmButton question={t("confirmRemoveRole", { role: role.name, key: role.applicationKey })} disabled={pending}>
        {t("remove")}
      </ConfirmButton>
      <Refusal state={state} />
    </form>
  );
}

/** The roles the group doesn't hold yet, grouped by application. */
export function AddGroupRoleForm({ groupId, choices }: { groupId: string; choices: ApplicationRoles[] }) {
  const t = useTranslations("Group");
  const [state, action, pending] = useActionState(addGroupRoleAction, undefined);
  if (choices.every(choice => choice.roles.length === 0)) {
    return null;
  }

  return (
    <form action={action}>
      <input type="hidden" name="groupId" value={groupId} />
      <RoleSelect choices={choices} label={t("role")} />{" "}
      <button type="submit" disabled={pending}>{t("addRole")}</button>
      <Refusal state={state} />
    </form>
  );
}
