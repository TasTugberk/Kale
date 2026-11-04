import { getTranslations } from "next-intl/server";
import { Card } from "@/components/ui/card";
import { PageHeader } from "@/components/ui/page-header";
import { listGroups } from "@/lib/groups";
import { adminApi } from "@/lib/server";
import { GroupsTable, NewGroupDialog } from "./forms";

export const dynamic = "force-dynamic";

export default async function GroupsPage() {
  const t = await getTranslations("Groups");
  const groups = await listGroups(adminApi);
  return (
    <>
      <PageHeader title={t("title")} description={t("explanation")} actions={<NewGroupDialog />} />
      <Card>
        <GroupsTable groups={groups} />
      </Card>
    </>
  );
}
