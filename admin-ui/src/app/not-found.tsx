import { getTranslations } from "next-intl/server";

export default async function NotFoundPage() {
  const t = await getTranslations("NotFound");
  return (
    <>
      <h1>{t("title")}</h1>
      <a href="/">{t("back")}</a>
    </>
  );
}
