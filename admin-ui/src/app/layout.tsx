import type { ReactNode } from "react";
import { NextIntlClientProvider } from "next-intl";
import { getLocale } from "next-intl/server";
import { AppShell } from "@/components/app-shell";
import { isDevelopmentOrTesting } from "@/lib/config";
import "./globals.css";

export const metadata = { title: "Kale" };

export default async function RootLayout({ children }: { children: ReactNode }) {
  const locale = await getLocale();
  return (
    <html lang={locale}>
      <body>
        {/* Gives the client components (menu, forms, tables) the same locale and messages as the server. */}
        <NextIntlClientProvider>
          <AppShell showTestToken={isDevelopmentOrTesting({ KALE_ENVIRONMENT: process.env.KALE_ENVIRONMENT })}>
            {children}
          </AppShell>
        </NextIntlClientProvider>
      </body>
    </html>
  );
}
