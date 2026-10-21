import type { ReactNode } from "react";
import { NextIntlClientProvider } from "next-intl";
import { getLocale, getTranslations } from "next-intl/server";
import "./globals.css";

export const metadata = { title: "Kale" };

export default async function RootLayout({ children }: { children: ReactNode }) {
  const locale = await getLocale();
  const t = await getTranslations("App");
  return (
    <html lang={locale}>
      <body>
        <header>
          <strong>{t("title")}</strong>
          <a href="/">{t("home")}</a>
          <a href="/applications">{t("applications")}</a>
          <a href="/users">{t("users")}</a>
          <a href="/groups">{t("groups")}</a>
          <a href="/sessions">{t("sessions")}</a>
          <span className="spacer" />
          <span>
            {t("language")}: <a href="/language?to=tr">TR</a> | <a href="/language?to=en">EN</a>
          </span>
          <form method="post" action="/logout">
            <button type="submit">{t("signOut")}</button>
          </form>
        </header>
        {/* Gives the client components (forms) the same locale and messages as the server. */}
        <NextIntlClientProvider>
          <main>{children}</main>
        </NextIntlClientProvider>
      </body>
    </html>
  );
}
