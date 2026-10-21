import { getRequestConfig } from "next-intl/server";
import { cookies, headers } from "next/headers";
import { timeZoneFrom } from "@/lib/config";
import { localeCookie, pickLocale } from "./locale";

export default getRequestConfig(async () => {
  const locale = pickLocale((await cookies()).get(localeCookie)?.value, (await headers()).get("accept-language") ?? "");
  return { locale, messages: (await import(`../../messages/${locale}.json`)).default, timeZone: timeZoneFrom({ KALE_TIME_ZONE: process.env.KALE_TIME_ZONE }) };
});
