import { getTranslations } from "next-intl/server";
import { listApplications } from "@/lib/applications";
import { adminApi } from "@/lib/server";
import { ApplicationActions, NewApplicationForm } from "./forms";

export const dynamic = "force-dynamic";

export default async function ApplicationsPage() {
  const t = await getTranslations("Applications");
  const applications = await listApplications(adminApi);
  return (
    <>
      <h1>{t("title")}</h1>
      <table>
        <thead>
          <tr>
            <th>{t("key")}</th>
            <th>{t("name")}</th>
            <th>{t("status")}</th>
            <th />
          </tr>
        </thead>
        <tbody>
          {applications.map(application => (
            <tr key={application.id}>
              <td><code>{application.key}</code></td>
              <td>{application.name}</td>
              <td>{application.isActive ? t("active") : t("inactive")}</td>
              <td><ApplicationActions application={application} /></td>
            </tr>
          ))}
        </tbody>
      </table>
      <h2>{t("new")}</h2>
      <NewApplicationForm />
    </>
  );
}
