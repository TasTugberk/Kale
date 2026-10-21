import { getTranslations } from "next-intl/server";
import { adminApi } from "@/lib/server";
import { listUsers } from "@/lib/users";
import { NewUserForm } from "./forms";

export const dynamic = "force-dynamic";

export default async function UsersPage() {
  const t = await getTranslations("Users");
  const users = await listUsers(adminApi);
  return (
    <>
      <h1>{t("title")}</h1>
      <table>
        <thead>
          <tr>
            <th>{t("userName")}</th>
            <th>{t("displayName")}</th>
            <th>{t("status")}</th>
          </tr>
        </thead>
        <tbody>
          {users.map(user => (
            <tr key={user.id}>
              <td><a href={`/users/${encodeURIComponent(user.id)}`}>{user.userName}</a></td>
              <td>{user.displayName}</td>
              <td>{user.isActive ? t("active") : t("disabled")}</td>
            </tr>
          ))}
        </tbody>
      </table>
      <h2>{t("new")}</h2>
      <NewUserForm />
    </>
  );
}
