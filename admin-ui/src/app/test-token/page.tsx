import { notFound } from "next/navigation";
import { getTranslations } from "next-intl/server";
import { Badge } from "@/components/ui/badge";
import { Card, CardBody } from "@/components/ui/card";
import { PageHeader } from "@/components/ui/page-header";
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
      <PageHeader
        title={<span className="flex items-center gap-3">{t("title")} <Badge tone="amber">{t("developmentOnly")}</Badge></span>}
        description={t("explanation")}
      />
      <Card className="max-w-2xl">
        <CardBody>
          <p className="mb-5 rounded-md bg-amber-50 px-3 py-2 text-sm text-amber-800">{t("sideEffect")}</p>
          <TestTokenForm choices={choices} users={users} />
        </CardBody>
      </Card>
    </>
  );
}
