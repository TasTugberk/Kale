import { getRequestConfig } from "next-intl/server";
import { cookies, headers } from "next/headers";

export const locales = ["tr", "en"] as const;
export type Locale = (typeof locales)[number];
export const localeCookie = "kale_locale";

/** Turkish unless the admin chose English (cookie) or their browser asks for it first. */
export default getRequestConfig(async () => {
  const chosen = (await cookies()).get(localeCookie)?.value;
  const accepted = ((await headers()).get("accept-language") ?? "").toLowerCase();
  const locale: Locale = chosen === "en" || chosen === "tr" ? chosen : accepted.startsWith("en") ? "en" : "tr";
  return { locale, messages: (await import(`../../messages/${locale}.json`)).default };
});
