import { getTranslations } from "next-intl/server";
import { adminApi } from "@/lib/server";

export const dynamic = "force-dynamic";

export default async function HomePage() {
  const t = await getTranslations("Home");
  const me = (await (await adminApi("me")).json()) as { userName: string; displayName: string };
  return <p>{t("signedInAs", { displayName: me.displayName, userName: me.userName })}</p>;
}
