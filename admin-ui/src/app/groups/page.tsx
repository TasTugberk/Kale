import { getTranslations } from "next-intl/server";
import { listGroups } from "@/lib/groups";
import { adminApi } from "@/lib/server";
import { NewGroupForm } from "./forms";

export const dynamic = "force-dynamic";

export default async function GroupsPage() {
  const t = await getTranslations("Groups");
  const groups = await listGroups(adminApi);
  return (
    <>
      <h1>{t("title")}</h1>
      <p className="hint">{t("explanation")}</p>
      {groups.length === 0
        ? <p className="hint">{t("noGroups")}</p>
        : (
          <table>
            <tbody>
              {groups.map(group => (
                <tr key={group.id}>
                  <td><a href={`/groups/${encodeURIComponent(group.id)}`}>{group.name}</a></td>
                  <td className="hint">{group.description}</td>
                </tr>
              ))}
            </tbody>
          </table>
        )}
      <h2>{t("new")}</h2>
      <NewGroupForm />
    </>
  );
}
