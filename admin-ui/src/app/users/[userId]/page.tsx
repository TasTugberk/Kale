import { notFound } from "next/navigation";
import { getTranslations } from "next-intl/server";
import { Activity } from "lucide-react";
import { roleChoices } from "@/components/role-choices";
import { Code } from "@/components/ui/badge";
import { ButtonLink } from "@/components/ui/button";
import { Card, CardHeader, Empty } from "@/components/ui/card";
import { PageHeader } from "@/components/ui/page-header";
import { listRolesByApplication, rolesNotHeld } from "@/lib/roles";
import { adminApi } from "@/lib/server";
import { getUser } from "@/lib/users";
import { UserStatus } from "../forms";
import { AccessForm, AssignRolesDialog, NewAccessForm, SetPasswordForm, UnassignRoleButton, UserActiveButton } from "./forms";

export const dynamic = "force-dynamic";

export default async function UserPage({ params }: { params: Promise<{ userId: string }> }) {
  const { userId } = await params;
  const t = await getTranslations("User");
  const user = await getUser(adminApi, userId);
  if (!user) {
    notFound();
  }

  // For the default-role and assign-role choices.
  const all = await listRolesByApplication(adminApi);
  const applications = all.map(choice => choice.application);

  const withAccess = new Set(user.applications.map(access => access.applicationId));
  const rolesOf = (applicationId: string) => all.find(choice => choice.application.id === applicationId)?.roles ?? [];
  const unassigned = roleChoices(rolesNotHeld(all, user.roles.map(role => role.roleId)));

  return (
    <>
      <PageHeader
        back={{ href: "/users", label: t("back") }}
        title={<span className="flex flex-wrap items-center gap-3">{user.displayName} <UserStatus isActive={user.isActive} /></span>}
        description={<>{user.userName}{!user.isActive && <> · {t("disabledExplanation")}</>}</>}
        actions={
          <>
            <ButtonLink href={`/sessions?userId=${encodeURIComponent(user.id)}`} size="md"><Activity aria-hidden />{t("sessions")}</ButtonLink>
            <UserActiveButton user={user} />
          </>
        }
      />
      <div className="grid gap-6 lg:grid-cols-2">
        <Card className="lg:col-span-2">
          <CardHeader title={t("applications")} description={t("applicationsHint")}
            actions={<NewAccessForm userId={user.id} applications={applications.filter(application => !withAccess.has(application.id))} />} />
          {user.applications.length === 0
            ? <Empty>{t("noApplications")}</Empty>
            : (
              <ul className="divide-y divide-slate-100">
                {user.applications.map(access => (
                  <li key={access.applicationId} data-application={access.applicationKey} className="flex flex-wrap items-center justify-between gap-3 px-5 py-3">
                    <Code>{access.applicationKey}</Code>
                    <AccessForm userId={user.id} access={access} roles={rolesOf(access.applicationId)} />
                  </li>
                ))}
              </ul>
            )}
        </Card>
        <Card>
          <CardHeader title={t("roles")} description={t("rolesHint")} actions={<AssignRolesDialog userId={user.id} choices={unassigned} />} />
          {user.roles.length === 0
            ? <Empty>{t("noRoles")}</Empty>
            : (
              <ul className="divide-y divide-slate-100">
                {user.roles.map(role => (
                  <li key={role.roleId} className="flex items-center justify-between gap-3 px-5 py-3">
                    <span className="flex items-center gap-2"><Code>{role.applicationKey}</Code> {role.name}</span>
                    <UnassignRoleButton userId={user.id} role={role} />
                  </li>
                ))}
              </ul>
            )}
        </Card>
        <Card>
          <CardHeader title={t("groups")} description={t("groupsHint")} />
          {user.groups.length === 0
            ? <Empty>{t("noGroups")}</Empty>
            : (
              <ul className="divide-y divide-slate-100">
                {user.groups.map(group => (
                  <li key={group.groupId} className="px-5 py-3"><a href={`/groups/${encodeURIComponent(group.groupId)}`} className="font-medium">{group.name}</a></li>
                ))}
              </ul>
            )}
        </Card>
        <Card>
          <CardHeader title={t("password")} description={t("passwordHint")} />
          <SetPasswordForm user={user} />
        </Card>
      </div>
    </>
  );
}
