import { notFound } from "next/navigation";
import { getTranslations } from "next-intl/server";
import { listApplications } from "@/lib/applications";
import { listOperations, listRoles } from "@/lib/roles";
import { adminApi } from "@/lib/server";
import { NewRoleForm, RoleForms } from "./forms";

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
      <p><a href="/applications">{t("back")}</a></p>
      <h1>{t("title", { key: application.key, name: application.name })}</h1>

      <h2>{t("operations")}</h2>
      {operations.length === 0
        ? <p className="hint">{t("noOperations")}</p>
        : (
          <ul>
            {operations.map(operation => (
              <li key={operation.id}>
                <code>{operation.name}</code>
                {operation.isObsolete && <span className="hint"> {t("obsolete")}</span>}
              </li>
            ))}
          </ul>
        )}

      <h2>{t("roles")}</h2>
      {roles.length === 0 && <p className="hint">{t("noRoles")}</p>}
      {roles.map(role => (
        <section key={role.id} className="item">
          <h3>{role.name}</h3>
          {role.description && <p className="hint">{role.description}</p>}
          <RoleForms role={role} operations={operations} />
        </section>
      ))}

      <h2>{t("new")}</h2>
      <NewRoleForm applicationId={applicationId} />
    </>
  );
}
