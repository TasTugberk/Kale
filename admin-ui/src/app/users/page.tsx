import { redirect } from "next/navigation";
import { getTranslations } from "next-intl/server";
import { Card } from "@/components/ui/card";
import { PageHeader } from "@/components/ui/page-header";
import { lastPageIfPast, listHref, listQueryFrom } from "@/lib/paging";
import { adminApi } from "@/lib/server";
import { listUsers, userSorts } from "@/lib/users";
import { NewUserDialog, UsersTable } from "./forms";

export const dynamic = "force-dynamic";

/** One page of users; ?search=, ?page=, ?sort= and ?desc= come from the address (see lib/paging.ts). */
export default async function UsersPage({ searchParams }: {
  searchParams: Promise<{ search?: string; page?: string; sort?: string; desc?: string }>;
}) {
  const t = await getTranslations("Users");
  const query = listQueryFrom(await searchParams, userSorts);
  const users = await listUsers(adminApi, query);
  const last = lastPageIfPast(query.page, users.total, users.pageSize);
  if (last !== undefined) {
    redirect(listHref("/users", { ...query, page: last }));
  }
  return (
    <>
      <PageHeader title={t("title")} description={t("explanation")} actions={<NewUserDialog />} />
      <Card>
        <UsersTable users={users} query={query} />
      </Card>
    </>
  );
}
