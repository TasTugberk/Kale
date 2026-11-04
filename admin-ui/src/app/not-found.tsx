import { getTranslations } from "next-intl/server";
import { SearchX } from "lucide-react";
import { ButtonLink } from "@/components/ui/button";

export default async function NotFoundPage() {
  const t = await getTranslations("NotFound");
  return (
    <div className="mx-auto mt-16 max-w-md text-center">
      <SearchX className="mx-auto size-10 text-slate-400" aria-hidden />
      <h1 className="mt-4 text-xl font-semibold text-slate-900">{t("title")}</h1>
      <ButtonLink href="/" variant="primary" size="md" className="mt-6">{t("back")}</ButtonLink>
    </div>
  );
}
