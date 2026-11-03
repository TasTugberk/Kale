"use client";

import { useActionState } from "react";
import { useTranslations } from "next-intl";
import { Plus, Power, Trash2 } from "lucide-react";
import { ConfirmButton, Refusal, Saved } from "@/components/form-parts";
import { PickDialog } from "@/components/pick-dialog";
import { Button } from "@/components/ui/button";
import { Select } from "@/components/ui/fields";
import type { Choice } from "@/components/ui/multi-select";
import type { Application } from "@/lib/applications";
import type { Role } from "@/lib/roles";
import type { Access, AssignedRole, User } from "@/lib/users";
import { assignRolesAction, setAccessAction, setUserActiveAction, unassignRoleAction } from "../actions";

export function UserActiveButton({ user }: { user: User }) {
  const t = useTranslations("User");
  const [state, action, pending] = useActionState(setUserActiveAction, undefined);
  return (
    <form action={action} className="grid justify-items-end gap-2">
      <input type="hidden" name="userId" value={user.id} />
      <input type="hidden" name="isActive" value={String(!user.isActive)} />
      {user.isActive
        ? (
          <ConfirmButton question={t("confirmDisable", { userName: user.userName })} disabled={pending} variant="dangerOutline" size="md">
            <Power aria-hidden />{t("disable")}
          </ConfirmButton>
        )
        : <Button type="submit" variant="secondary" disabled={pending}><Power aria-hidden />{t("enable")}</Button>}
      <Refusal state={state} />
    </form>
  );
}

/** One application the user has access to: active or not, and the default role. */
export function AccessForm({ userId, access, roles }: { userId: string; access: Access; roles: Role[] }) {
  const t = useTranslations("User");
  const [state, action, pending] = useActionState(setAccessAction, undefined);
  const shown = state?.submitted ?? access;
  // React resets a form after its action, and a <select> resets to the option it started with: React applies a
  // select's defaultValue only when it's created. The keys create them again whenever the shown values change.
  return (
    <form action={action} className="grid justify-items-end gap-2">
      <input type="hidden" name="userId" value={userId} />
      <input type="hidden" name="applicationId" value={access.applicationId} />
      <div className="flex flex-wrap items-center gap-2">
        <Saved state={state?.outcome} />
        <Select key={`active-${shown.isActive}`} name="isActive" aria-label={t("access")} defaultValue={String(shown.isActive)}>
          <option value="true">{t("accessActive")}</option>
          <option value="false">{t("accessInactive")}</option>
        </Select>
        <Select key={`role-${shown.defaultRoleId}`} name="defaultRoleId" aria-label={t("defaultRole")} defaultValue={shown.defaultRoleId ?? ""}>
          <option value="">{t("noDefaultRole")}</option>
          {roles.map(role => <option key={role.id} value={role.id}>{role.name}</option>)}
        </Select>
        <ConfirmButton question={t("confirmAccess", { key: access.applicationKey })} disabled={pending} size="md">{t("save")}</ConfirmButton>
      </div>
      <Refusal state={state?.outcome} />
    </form>
  );
}

/** Access to an application the user has none for yet (active, no default role). */
export function NewAccessForm({ userId, applications }: { userId: string; applications: Application[] }) {
  const t = useTranslations("User");
  const [state, action, pending] = useActionState(setAccessAction, undefined);
  if (applications.length === 0) {
    return null;
  }

  return (
    <form action={action} className="grid justify-items-end gap-2">
      <input type="hidden" name="userId" value={userId} />
      <input type="hidden" name="isActive" value="true" />
      <div className="flex items-center gap-2">
        <Select name="applicationId" aria-label={t("application")} required className="font-mono text-xs">
          {applications.map(application => <option key={application.id} value={application.id}>{application.key}</option>)}
        </Select>
        <Button type="submit" size="md" disabled={pending}><Plus aria-hidden />{t("giveAccess")}</Button>
      </div>
      <Refusal state={state?.outcome} />
    </form>
  );
}

export function UnassignRoleButton({ userId, role }: { userId: string; role: AssignedRole }) {
  const t = useTranslations("User");
  const [state, action, pending] = useActionState(unassignRoleAction, undefined);
  return (
    <form action={action} className="grid justify-items-end gap-1">
      <input type="hidden" name="userId" value={userId} />
      <input type="hidden" name="roleId" value={role.roleId} />
      <ConfirmButton question={t("confirmUnassign", { role: role.name, key: role.applicationKey })} disabled={pending} variant="ghost">
        <Trash2 aria-hidden />{t("unassign")}
      </ConfirmButton>
      <Refusal state={state} />
    </form>
  );
}

/** The roles the user doesn't hold directly yet, listed under their application. */
export function AssignRolesDialog({ userId, choices }: { userId: string; choices: Choice[] }) {
  const t = useTranslations("User");
  return (
    <PickDialog triggerLabel={t("assign")} title={t("assignTitle")} description={t("assignExplanation")}
      action={assignRolesAction} hidden={{ userId }} name="roleId" label={t("roles")} placeholder={t("pickRoles")} choices={choices}
      submitLabel={count => t("assignPicked", { count })} nothingLeft={t("noRolesLeft")} />
  );
}
