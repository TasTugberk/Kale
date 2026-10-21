"use client";

import { useActionState } from "react";
import { useTranslations } from "next-intl";
import { ConfirmButton, Refusal, Saved } from "@/components/form-parts";
import { RoleSelect } from "@/components/role-select";
import type { Application } from "@/lib/applications";
import type { ApplicationRoles, Role } from "@/lib/roles";
import type { Access, AssignedRole, User } from "@/lib/users";
import { assignRoleAction, setAccessAction, setUserActiveAction, unassignRoleAction } from "../actions";

export function UserActiveForm({ user }: { user: User }) {
  const t = useTranslations("User");
  const [state, action, pending] = useActionState(setUserActiveAction, undefined);
  return (
    <form action={action}>
      <input type="hidden" name="userId" value={user.id} />
      <input type="hidden" name="isActive" value={String(!user.isActive)} />
      {user.isActive
        ? <ConfirmButton question={t("confirmDisable", { userName: user.userName })} disabled={pending}>{t("disable")}</ConfirmButton>
        : <button type="submit" disabled={pending}>{t("enable")}</button>}
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
    <form action={action} className="inline">
      <input type="hidden" name="userId" value={userId} />
      <input type="hidden" name="applicationId" value={access.applicationId} />
      <select key={`active-${shown.isActive}`} name="isActive" aria-label={t("access")} defaultValue={String(shown.isActive)}>
        <option value="true">{t("accessActive")}</option>
        <option value="false">{t("accessInactive")}</option>
      </select>{" "}
      <select key={`role-${shown.defaultRoleId}`} name="defaultRoleId" aria-label={t("defaultRole")} defaultValue={shown.defaultRoleId ?? ""}>
        <option value="">{t("noDefaultRole")}</option>
        {roles.map(role => <option key={role.id} value={role.id}>{role.name}</option>)}
      </select>{" "}
      <ConfirmButton question={t("confirmAccess", { key: access.applicationKey })} disabled={pending}>{t("save")}</ConfirmButton>
      <Refusal state={state?.outcome} />
      <Saved state={state?.outcome} />
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
    <form action={action}>
      <input type="hidden" name="userId" value={userId} />
      <input type="hidden" name="isActive" value="true" />
      <select name="applicationId" aria-label={t("application")} required>
        {applications.map(application => <option key={application.id} value={application.id}>{application.key}</option>)}
      </select>{" "}
      <button type="submit" disabled={pending}>{t("giveAccess")}</button>
      <Refusal state={state?.outcome} />
    </form>
  );
}

export function UnassignRoleForm({ userId, role }: { userId: string; role: AssignedRole }) {
  const t = useTranslations("User");
  const [state, action, pending] = useActionState(unassignRoleAction, undefined);
  return (
    <form action={action} className="inline">
      <input type="hidden" name="userId" value={userId} />
      <input type="hidden" name="roleId" value={role.roleId} />
      <ConfirmButton question={t("confirmUnassign", { role: role.name, key: role.applicationKey })} disabled={pending}>
        {t("unassign")}
      </ConfirmButton>
      <Refusal state={state} />
    </form>
  );
}

/** The roles the user doesn't hold directly yet, grouped by application. */
export function AssignRoleForm({ userId, choices }: { userId: string; choices: ApplicationRoles[] }) {
  const t = useTranslations("User");
  const [state, action, pending] = useActionState(assignRoleAction, undefined);
  if (choices.every(choice => choice.roles.length === 0)) {
    return null;
  }

  return (
    <form action={action}>
      <input type="hidden" name="userId" value={userId} />
      <RoleSelect choices={choices} label={t("role")} />{" "}
      <button type="submit" disabled={pending}>{t("assign")}</button>
      <Refusal state={state} />
    </form>
  );
}
