import { notFound } from "next/navigation";
import { getTranslations } from "next-intl/server";
import { Code } from "@/components/ui/badge";
import { Card, CardHeader } from "@/components/ui/card";
import { PageHeader } from "@/components/ui/page-header";
import { listApplications } from "@/lib/applications";
import { listOperations, listRoles } from "@/lib/roles";
import { adminApi } from "@/lib/server";
import { NewRoleDialog, OperationsTable, RolesTable } from "./forms";

export const dynamic = "force-dynamic";

export default async function RolesPage({ params }: { params: Promise<{ applicationId: string }> }) {
  const { applicationId } = await params;
  const t = await getTranslations("Roles");

  // There's no single-application endpoint; the list is short.
  const application = (await listApplications(adminApi)).find(a => a.id === applicationId);
  if (!application) {
    notFound();
  }

  const [operations, roles] = await Promise.all([listOperations(adminApi, applicationId), listRoles(adminApi, applicationId)]);
  return (
    <>
      <PageHeader
        back={{ href: `/applications/${encodeURIComponent(applicationId)}`, label: application.name }}
        title={<span className="flex flex-wrap items-center gap-3">{t("title")} <Code className="text-sm">{application.key}</Code></span>}
        description={t("explanation")}
        actions={<NewRoleDialog applicationId={applicationId} />}
      />
      <div className="grid gap-6">
        <Card>
          <CardHeader title={t("roles")} />
          <RolesTable applicationId={applicationId} roles={roles} />
        </Card>
        <Card>
          <CardHeader title={t("operations")} description={t("operationsExplanation")} />
          <OperationsTable operations={operations} />
        </Card>
      </div>
    </>
  );
}
