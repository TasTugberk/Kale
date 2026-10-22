import { getTranslations } from "next-intl/server";

/**
 * After signing out. state=not-admin: the UI signed out someone who can't administer Kale; state=session-ended:
 * AuthService ended the session. Either way the link asks for the password again (prompt=login).
 */
export default async function SignedOutPage({ searchParams }: { searchParams: Promise<{ state?: string }> }) {
  const t = await getTranslations("SignedOut");
  const { state } = await searchParams;
  return (
    <>
      <h1>{state === "not-admin" ? t("notAdminTitle") : state === "session-ended" ? t("sessionEndedTitle") : t("title")}</h1>
      {state === "not-admin" && <p>{t("notAdminExplanation")}</p>}
      {state === "session-ended" && <p>{t("sessionEndedExplanation")}</p>}
      <a href="/login?prompt=login">{t("signInAgain")}</a>
    </>
  );
}
