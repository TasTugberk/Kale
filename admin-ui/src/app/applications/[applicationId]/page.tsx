import { notFound } from "next/navigation";
import { getTranslations } from "next-intl/server";
import { adminUiApplicationKey, getApplication } from "@/lib/applications";
import { adminApi } from "@/lib/server";
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
      <p><a href="/applications">{t("back")}</a></p>
      <h1>{application.name} <span className="hint">(<code>{application.key}</code>)</span></h1>
      <p className="hint">{t("keyIsPermanent")}</p>
      {application.key === adminUiApplicationKey && <p className="hint">{t("adminUiAddresses")}</p>}
      <EditApplicationForm application={application} />
      <p><a href={`/applications/${encodeURIComponent(application.id)}/roles`}>{t("roles")}</a></p>
    </>
  );
}
