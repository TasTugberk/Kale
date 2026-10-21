import { notFound } from "next/navigation";
import { getTranslations } from "next-intl/server";
import { getGroup } from "@/lib/groups";
import { listRolesByApplication, rolesNotHeld } from "@/lib/roles";
import { adminApi } from "@/lib/server";
import { listUsers } from "@/lib/users";
import {
  AddGroupRoleForm, AddMemberForm, DeleteGroupForm, RemoveGroupRoleForm, RemoveMemberForm, RenameGroupForm,
} from "./forms";

export const dynamic = "force-dynamic";

export default async function GroupPage({ params }: { params: Promise<{ groupId: string }> }) {
  const { groupId } = await params;
  const t = await getTranslations("Group");
  const group = await getGroup(adminApi, groupId);
  if (!group) {
    notFound();
  }

  const [users, all] = await Promise.all([listUsers(adminApi), listRolesByApplication(adminApi)]);
  const members = new Set(group.members.map(member => member.userId));
  const unheld = rolesNotHeld(all, group.roles.map(role => role.roleId));

  return (
    <>
      <p><a href="/groups">{t("back")}</a></p>
      <h1>{group.name}</h1>
      {group.description && <p className="hint">{group.description}</p>}
      <RenameGroupForm group={group} /> <DeleteGroupForm group={group} />

      <h2>{t("members")}</h2>
      {group.members.length === 0
        ? <p className="hint">{t("noMembers")}</p>
        : (
          <table>
            <tbody>
              {group.members.map(member => (
                <tr key={member.userId}>
                  <td><a href={`/users/${encodeURIComponent(member.userId)}`}>{member.userName}</a></td>
                  <td>{member.displayName}</td>
                  <td><RemoveMemberForm groupId={group.id} member={member} /></td>
                </tr>
              ))}
            </tbody>
          </table>
        )}
      <AddMemberForm groupId={group.id} users={users.filter(user => !members.has(user.id))} />

      <h2>{t("roles")}</h2>
      <p className="hint">{t("rolesHint")}</p>
      {group.roles.length === 0
        ? <p className="hint">{t("noRoles")}</p>
        : (
          <table>
            <tbody>
              {group.roles.map(role => (
                <tr key={role.roleId}>
                  <td><code>{role.applicationKey}</code></td>
                  <td>{role.name}</td>
                  <td><RemoveGroupRoleForm groupId={group.id} role={role} /></td>
                </tr>
              ))}
            </tbody>
          </table>
        )}
      <AddGroupRoleForm groupId={group.id} choices={unheld} />
    </>
  );
}
