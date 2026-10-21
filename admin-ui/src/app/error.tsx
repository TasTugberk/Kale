"use client";

import { useTranslations } from "next-intl";

/** When a page can't load or a change fails unexpectedly (e.g. AuthService is unreachable). */
export default function ErrorPage({ reset }: { error: Error; reset: () => void }) {
  const t = useTranslations("Error");
  return (
    <>
      <h1>{t("title")}</h1>
      <p>{t("explanation")}</p>
      <button type="button" onClick={reset}>{t("tryAgain")}</button>
    </>
  );
}
