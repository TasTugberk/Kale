import { notFound } from "next/navigation";
import { getTranslations } from "next-intl/server";
import { roleChoices } from "@/components/role-choices";
import { Card, CardBody, CardHeader } from "@/components/ui/card";
import { PageHeader } from "@/components/ui/page-header";
import { getGroup } from "@/lib/groups";
import { listRolesByApplication, rolesNotHeld } from "@/lib/roles";
import { adminApi } from "@/lib/server";
import { listUsers } from "@/lib/users";
import { AddGroupRolesDialog, AddMembersDialog, DeleteGroupButton, GroupRolesTable, MembersTable, RenameGroupForm } from "./forms";

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
  const notMembers = users.filter(user => !members.has(user.id));
  const unheld = roleChoices(rolesNotHeld(all, group.roles.map(role => role.roleId)));

  return (
    <>
      <PageHeader
        back={{ href: "/groups", label: t("back") }}
        title={group.name}
        description={group.description ?? undefined}
        actions={<DeleteGroupButton group={group} />}
      />
      <div className="grid gap-6 lg:grid-cols-3">
        <div className="grid content-start gap-6 lg:col-span-2">
          <Card>
            <CardHeader title={t("members")} actions={<AddMembersDialog groupId={group.id} users={notMembers} />} />
            <MembersTable groupId={group.id} members={group.members} />
          </Card>
          <Card>
            <CardHeader title={t("roles")} description={t("rolesHint")} actions={<AddGroupRolesDialog groupId={group.id} choices={unheld} />} />
            <GroupRolesTable groupId={group.id} roles={group.roles} />
          </Card>
        </div>
        <Card className="self-start">
          <CardHeader title={t("details")} />
          <CardBody><RenameGroupForm group={group} /></CardBody>
        </Card>
      </div>
    </>
  );
}
