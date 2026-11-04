"use client";

import { useTranslations } from "next-intl";
import { TriangleAlert } from "lucide-react";
import { Button } from "@/components/ui/button";

/** When a page can't load or a change fails unexpectedly (e.g. AuthService is unreachable). */
export default function ErrorPage({ reset }: { error: Error; reset: () => void }) {
  const t = useTranslations("Error");
  return (
    <div className="mx-auto mt-16 max-w-md text-center">
      <TriangleAlert className="mx-auto size-10 text-red-500" aria-hidden />
      <h1 className="mt-4 text-xl font-semibold text-slate-900">{t("title")}</h1>
      <p className="mt-2 text-sm text-slate-600">{t("explanation")}</p>
      <Button type="button" className="mt-6" onClick={reset}>{t("tryAgain")}</Button>
    </div>
  );
}
