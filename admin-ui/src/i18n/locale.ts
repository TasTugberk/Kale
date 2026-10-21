export const locales = ["tr", "en"] as const;
export type Locale = (typeof locales)[number];
export const localeCookie = "kale_locale";

/** The admin's choice (cookie) first; otherwise English only when the browser asks for it first, else Turkish. */
export function pickLocale(chosen: string | undefined, acceptLanguage: string): Locale {
  if (chosen === "tr" || chosen === "en") {
    return chosen;
  }

  if (acceptLanguage.toLowerCase().startsWith("en")) {
    return "en";
  }

  return "tr";
}
