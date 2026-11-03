import { notFound } from "next/navigation";
import { getTranslations } from "next-intl/server";
import { Card, CardBody, CardHeader } from "@/components/ui/card";
import { PageHeader } from "@/components/ui/page-header";
import { listApplications } from "@/lib/applications";
import { findRole, grantableOperations, listOperations, obsoleteGrants } from "@/lib/roles";
import { adminApi } from "@/lib/server";
import { AddOperationsDialog, DeleteRoleButton, RenameRoleForm, RoleOperationsTable } from "./forms";

export const dynamic = "force-dynamic";

/** One role: the operations it grants (added in a dialog, removed one by one), its name, and deleting it. */
export default async function RolePage({ params }: { params: Promise<{ applicationId: string; roleId: string }> }) {
  const { applicationId, roleId } = await params;
  const t = await getTranslations("Role");

  const application = (await listApplications(adminApi)).find(a => a.id === applicationId);
  if (!application) {
    notFound();
  }

  const [role, operations] = await Promise.all([findRole(adminApi, applicationId, roleId), listOperations(adminApi, applicationId)]);
  if (!role) {
    notFound();
  }

  const obsolete = obsoleteGrants(role.operations, operations);
  return (
    <>
      <PageHeader
        back={{ href: `/applications/${encodeURIComponent(applicationId)}/roles`, label: t("back", { key: application.key }) }}
        title={role.name}
        description={role.description ?? undefined}
        actions={<DeleteRoleButton applicationId={applicationId} role={role} />}
      />
      <div className="grid gap-6 lg:grid-cols-3">
        <Card className="lg:col-span-2">
          <CardHeader
            title={t("operations")}
            description={t("operationsExplanation")}
            actions={<AddOperationsDialog applicationId={applicationId} role={role} grantable={grantableOperations(role, operations)} obsolete={obsolete} />}
          />
          <RoleOperationsTable applicationId={applicationId} role={role} obsolete={obsolete} />
        </Card>
        <Card className="self-start">
          <CardHeader title={t("details")} />
          <CardBody><RenameRoleForm role={role} /></CardBody>
        </Card>
      </div>
    </>
  );
}
