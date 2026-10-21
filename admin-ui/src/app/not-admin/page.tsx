import { getTranslations } from "next-intl/server";

export default async function NotAdminPage() {
  const t = await getTranslations("NotAdmin");
  return (
    <>
      <h1>{t("title")}</h1>
      <p>{t("explanation")}</p>
      <a href="/login">{t("signInAgain")}</a>
    </>
  );
}
