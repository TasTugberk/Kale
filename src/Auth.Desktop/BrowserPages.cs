using System.Globalization;

namespace Auth.Desktop;

/// <summary>
/// The page the browser shows after coming back to the app: Turkish or English by the computer's language (Kale's
/// own pages speak both). Plain HTML without anything from the network.
/// </summary>
internal static class BrowserPages
{
    public static string SignedIn(CultureInfo culture) => IsTurkish(culture)
        ? Page("tr", "Giriş yapıldı", "Giriş yapıldı. Bu pencereyi kapatıp uygulamaya dönebilirsiniz.")
        : Page("en", "Signed in", "You're signed in. You can close this window and go back to the application.");

    public static string Failed(CultureInfo culture) => IsTurkish(culture)
        ? Page("tr", "Giriş yapılamadı", "Giriş yapılamadı. Bu pencereyi kapatıp uygulamada yeniden deneyin.")
        : Page("en", "Sign-in failed", "The sign-in didn't work. Close this window and try again in the application.");

    private static bool IsTurkish(CultureInfo culture) => culture.TwoLetterISOLanguageName == "tr";

    private static string Page(string language, string title, string text) => $"""
        <!doctype html>
        <html lang="{language}">
        <head><meta charset="utf-8"><meta name="viewport" content="width=device-width, initial-scale=1"><title>{title}</title></head>
        <body style="font-family: system-ui, sans-serif; max-width: 32rem; margin: 4rem auto; padding: 0 1rem;">
        <h1 style="font-size: 1.4rem;">{title}</h1>
        <p>{text}</p>
        </body>
        </html>
        """;
}
