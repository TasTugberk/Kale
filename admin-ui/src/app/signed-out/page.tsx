import { getTranslations } from "next-intl/server";

/** After signing out; state=not-admin when the UI signed out someone who can't administer Kale. */
export default async function SignedOutPage({ searchParams }: { searchParams: Promise<{ state?: string }> }) {
  const t = await getTranslations("SignedOut");
  const notAdmin = (await searchParams).state === "not-admin";
  return (
    <>
      <h1>{notAdmin ? t("notAdminTitle") : t("title")}</h1>
      {notAdmin && <p>{t("notAdminExplanation")}</p>}
      <a href="/login?prompt=login">{t("signInAgain")}</a>
    </>
  );
}
