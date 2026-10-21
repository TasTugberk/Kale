import { getTranslations } from "next-intl/server";

export default async function SignedOutPage() {
  const t = await getTranslations("SignedOut");
  return (
    <>
      <h1>{t("title")}</h1>
      <a href="/login">{t("signInAgain")}</a>
    </>
  );
}
