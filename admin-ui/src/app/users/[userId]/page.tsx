import { notFound } from "next/navigation";
import { getTranslations } from "next-intl/server";
import { listRolesByApplication, rolesNotHeld } from "@/lib/roles";
import { adminApi } from "@/lib/server";
import { getUser } from "@/lib/users";
import { AccessForm, AssignRoleForm, NewAccessForm, UnassignRoleForm, UserActiveForm } from "./forms";

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
  const unassigned = rolesNotHeld(all, user.roles.map(role => role.roleId));

  return (
    <>
      <p><a href="/users">{t("back")}</a></p>
      <h1>{user.displayName} <span className="hint">({user.userName})</span></h1>
      <p>{user.isActive ? t("active") : t("disabled")}</p>
      <UserActiveForm user={user} />
      <p><a href={`/sessions?userId=${encodeURIComponent(user.id)}`}>{t("sessions")}</a></p>

      <h2>{t("applications")}</h2>
      <p className="hint">{t("applicationsHint")}</p>
      {user.applications.length === 0
        ? <p className="hint">{t("noApplications")}</p>
        : (
          <table>
            <tbody>
              {user.applications.map(access => (
                <tr key={access.applicationId}>
                  <td><code>{access.applicationKey}</code></td>
                  <td><AccessForm userId={user.id} access={access} roles={rolesOf(access.applicationId)} /></td>
                </tr>
              ))}
            </tbody>
          </table>
        )}
      <NewAccessForm userId={user.id} applications={applications.filter(application => !withAccess.has(application.id))} />

      <h2>{t("roles")}</h2>
      {user.roles.length === 0
        ? <p className="hint">{t("noRoles")}</p>
        : (
          <table>
            <tbody>
              {user.roles.map(role => (
                <tr key={role.roleId}>
                  <td><code>{role.applicationKey}</code></td>
                  <td>{role.name}</td>
                  <td><UnassignRoleForm userId={user.id} role={role} /></td>
                </tr>
              ))}
            </tbody>
          </table>
        )}
      <AssignRoleForm userId={user.id} choices={unassigned} />

      <h2>{t("groups")}</h2>
      {user.groups.length === 0
        ? <p className="hint">{t("noGroups")}</p>
        : <ul>{user.groups.map(group => <li key={group.groupId}><a href={`/groups/${encodeURIComponent(group.groupId)}`}>{group.name}</a></li>)}</ul>}
    </>
  );
}
