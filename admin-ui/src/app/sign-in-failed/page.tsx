import { getTranslations } from "next-intl/server";
import { PublicPage } from "@/components/public-page";

export default async function SignInFailedPage() {
  const t = await getTranslations("SignInFailed");
  return <PublicPage title={t("title")} link={{ href: "/login", label: t("tryAgain") }} />;
}
