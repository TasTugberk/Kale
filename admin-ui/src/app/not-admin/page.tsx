import { getTranslations } from "next-intl/server";
import { PublicPage } from "@/components/public-page";

export default async function NotAdminPage() {
  const t = await getTranslations("NotAdmin");
  // prompt=login: AuthService asks for credentials instead of signing the same user in again.
  return <PublicPage title={t("title")} explanation={t("explanation")} link={{ href: "/login?prompt=login", label: t("signInAgain") }} />;
}
