import { getTranslations } from "next-intl/server";
import { PublicPage } from "@/components/public-page";

/**
 * After signing out. state=not-admin: the UI signed out someone who can't administer Kale; state=session-ended:
 * AuthService ended the session. Either way the link asks for the password again (prompt=login).
 */
export default async function SignedOutPage({ searchParams }: { searchParams: Promise<{ state?: string }> }) {
  const t = await getTranslations("SignedOut");
  const { state } = await searchParams;
  if (state === "session-ended") {
    return <PublicPage title={t("sessionEndedTitle")} explanation={t("sessionEndedExplanation")} link={{ href: "/login?prompt=login", label: t("sessionEndedSignIn") }} />;
  }

  const notAdmin = state === "not-admin";
  return (
    <PublicPage
      title={notAdmin ? t("notAdminTitle") : t("title")}
      explanation={notAdmin ? t("notAdminExplanation") : undefined}
      link={{ href: "/login?prompt=login", label: t("signInAgain") }}
    />
  );
}
