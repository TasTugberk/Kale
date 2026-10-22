import { getTranslations } from "next-intl/server";

/**
 * After signing out. state=not-admin: the UI signed out someone who can't administer Kale; state=session-ended:
 * AuthService ended the session. Either way the link asks for the password again (prompt=login).
 */
export default async function SignedOutPage({ searchParams }: { searchParams: Promise<{ state?: string }> }) {
  const t = await getTranslations("SignedOut");
  const { state } = await searchParams;
  if (state === "session-ended") {
    return (
      <>
        <h1>{t("sessionEndedTitle")}</h1>
        <p>{t("sessionEndedExplanation")}</p>
        <a href="/login?prompt=login">{t("sessionEndedSignIn")}</a>
      </>
    );
  }

  const notAdmin = state === "not-admin";
  return (
    <>
      <h1>{notAdmin ? t("notAdminTitle") : t("title")}</h1>
      {notAdmin && <p>{t("notAdminExplanation")}</p>}
      <a href="/login?prompt=login">{t("signInAgain")}</a>
    </>
  );
}
