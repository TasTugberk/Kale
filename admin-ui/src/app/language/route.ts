import { cookies } from "next/headers";
import { localeCookie } from "@/i18n/locale";
import { config, cookieOptions } from "@/lib/server";

/** Switches the language (?to=tr or ?to=en) for a year and returns to the home page. */
export async function GET(request: Request) {
  const to = new URL(request.url).searchParams.get("to");
  if (to === "tr" || to === "en") {
    (await cookies()).set(localeCookie, to, cookieOptions(365 * 24 * 60 * 60));
  }

  return Response.redirect(config().publicUrl.href, 302);
}
