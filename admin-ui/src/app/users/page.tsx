import { getTranslations } from "next-intl/server";
import { Card } from "@/components/ui/card";
import { PageHeader } from "@/components/ui/page-header";
import { adminApi } from "@/lib/server";
import { listUsers } from "@/lib/users";
import { NewUserDialog, UsersTable } from "./forms";

export const dynamic = "force-dynamic";

export default async function UsersPage() {
  const t = await getTranslations("Users");
  const users = await listUsers(adminApi);
  return (
    <>
      <PageHeader title={t("title")} description={t("explanation")} actions={<NewUserDialog />} />
      <Card>
        <UsersTable users={users} />
      </Card>
    </>
  );
}
