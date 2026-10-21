import { getTranslations } from "next-intl/server";

export default async function NotAdminPage() {
  const t = await getTranslations("NotAdmin");
  return (
    <>
      <h1>{t("title")}</h1>
      <p>{t("explanation")}</p>
      {/* prompt=login: AuthService asks for credentials instead of signing the same user in again. */}
      <a href="/login?prompt=login">{t("signInAgain")}</a>
    </>
  );
}
