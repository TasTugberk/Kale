import { notFound } from "next/navigation";
import { getTranslations } from "next-intl/server";
import { ShieldCheck } from "lucide-react";
import { Code } from "@/components/ui/badge";
import { ButtonLink } from "@/components/ui/button";
import { Card, CardBody, CardHeader } from "@/components/ui/card";
import { PageHeader } from "@/components/ui/page-header";
import { adminUiApplicationKey, getApplication } from "@/lib/applications";
import { adminApi } from "@/lib/server";
import { ActiveButton, RotateSecretButton, StatusBadge } from "../forms";
import { EditApplicationForm } from "./forms";

export const dynamic = "force-dynamic";

export default async function ApplicationPage({ params }: { params: Promise<{ applicationId: string }> }) {
  const { applicationId } = await params;
  const t = await getTranslations("Application");
  const application = await getApplication(adminApi, applicationId);
  if (!application) {
    notFound();
  }

  return (
    <>
      <PageHeader
        back={{ href: "/applications", label: t("back") }}
        title={<span className="flex flex-wrap items-center gap-3">{application.name} <Code className="text-sm">{application.key}</Code> <StatusBadge isActive={application.isActive} /></span>}
        description={t("keyIsPermanent")}
        actions={
          <ButtonLink href={`/applications/${encodeURIComponent(application.id)}/roles`} variant="primary" size="md">
            <ShieldCheck aria-hidden />{t("roles")}
          </ButtonLink>
        }
      />
      <div className="grid gap-6 lg:grid-cols-3">
        <Card className="lg:col-span-2">
          <CardHeader title={t("details")} description={application.key === adminUiApplicationKey ? t("adminUiAddresses") : undefined} />
          <CardBody>
            <EditApplicationForm application={application} />
          </CardBody>
        </Card>
        <div className="grid content-start gap-6">
          <Card>
            <CardHeader title={t("secret")} description={t("secretExplanation")} />
            <CardBody><RotateSecretButton application={application} /></CardBody>
          </Card>
          <Card>
            <CardHeader title={t("status")} description={application.isActive ? t("activeExplanation") : t("inactiveExplanation")} />
            <CardBody><ActiveButton application={application} size="md" /></CardBody>
          </Card>
        </div>
      </div>
    </>
  );
}
