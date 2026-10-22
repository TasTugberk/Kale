import { notFound } from "next/navigation";
import { getTranslations } from "next-intl/server";
import { isDevelopmentOrTesting } from "@/lib/config";
import { listRolesByApplication } from "@/lib/roles";
import { adminApi } from "@/lib/server";
import { listUsers } from "@/lib/users";
import { TestTokenForm } from "./forms";

export const dynamic = "force-dynamic";

/** Development/Testing only: a user's access token to an application, to try an API without a sign-in page. */
export default async function TestTokenPage() {
  if (!isDevelopmentOrTesting({ KALE_ENVIRONMENT: process.env.KALE_ENVIRONMENT })) {
    notFound();
  }

  const t = await getTranslations("TestToken");
  const [choices, users] = await Promise.all([listRolesByApplication(adminApi), listUsers(adminApi)]);
  return (
    <>
      <h1>{t("title")}</h1>
      <p className="hint">{t("explanation")}</p>
      <p className="hint">{t("sideEffect")}</p>
      <TestTokenForm choices={choices} users={users} />
    </>
  );
}
