import { getTranslations } from "next-intl/server";
import { Card } from "@/components/ui/card";
import { PageHeader } from "@/components/ui/page-header";
import { listApplications } from "@/lib/applications";
import { adminApi } from "@/lib/server";
import { ApplicationsTable, NewApplicationDialog } from "./forms";

export const dynamic = "force-dynamic";

export default async function ApplicationsPage() {
  const t = await getTranslations("Applications");
  const applications = await listApplications(adminApi);
  return (
    <>
      <PageHeader title={t("title")} description={t("explanation")} actions={<NewApplicationDialog />} />
      <Card>
        <ApplicationsTable applications={applications} />
      </Card>
    </>
  );
}
