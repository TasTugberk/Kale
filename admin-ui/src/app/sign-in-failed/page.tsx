import { getTranslations } from "next-intl/server";

export default async function SignInFailedPage() {
  const t = await getTranslations("SignInFailed");
  return (
    <>
      <h1>{t("title")}</h1>
      <a href="/login">{t("tryAgain")}</a>
    </>
  );
}
