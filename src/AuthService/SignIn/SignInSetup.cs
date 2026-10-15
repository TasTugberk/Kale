using System.Globalization;
using System.Text.Encodings.Web;
using System.Text.Unicode;
using AuthService.Domain;
using AuthService.Persistence;
using Microsoft.AspNetCore.Authentication.Cookies;
using Microsoft.AspNetCore.Identity;
using Microsoft.AspNetCore.Localization;
using Microsoft.Extensions.WebEncoders;

namespace AuthService.SignIn;

/// <summary>Users signing in to AuthService itself: Identity, its cookie, and the TR/EN login pages.</summary>
public static class SignInSetup
{
    public const string LoginPath = "/account/login";

    public static void AddUserSignIn(this IServiceCollection services, IHostEnvironment environment)
    {
        services
            .AddIdentityCore<User>(options =>
            {
                // Lockout slows down password guessing: 5 wrong attempts lock the account for 15 minutes.
                options.Lockout.MaxFailedAccessAttempts = 5;
                options.Lockout.DefaultLockoutTimeSpan = TimeSpan.FromMinutes(15);
                options.Lockout.AllowedForNewUsers = true;

                // Length matters most for passwords (NIST SP 800-63B); other rules stay at Identity's defaults.
                options.Password.RequiredLength = 12;
            })
            .AddEntityFrameworkStores<AuthDbContext>()
            .AddSignInManager();

        // The cookie only says "this browser is signed in to AuthService"; apps never see it. They get tokens.
        services.AddAuthentication(IdentityConstants.ApplicationScheme).AddIdentityCookies();
        services.ConfigureApplicationCookie(cookie =>
        {
            cookie.LoginPath = LoginPath;
            cookie.Cookie.Name = "kale.signin";
            cookie.Cookie.HttpOnly = true;
            cookie.Cookie.SameSite = SameSiteMode.Lax;

            // Only over HTTPS, except the plain-HTTP test server and local development.
            cookie.Cookie.SecurePolicy = environment.IsDevelopment() || environment.IsEnvironment("Testing")
                ? CookieSecurePolicy.SameAsRequest
                : CookieSecurePolicy.Always;
            cookie.SlidingExpiration = false;
        });

        // Signed in to AuthService for as long as a session lasts (Sessions:Lifetime), without extending it.
        services.AddOptions<CookieAuthenticationOptions>(IdentityConstants.ApplicationScheme)
            .Configure<SessionSettings>((cookie, sessions) => cookie.ExpireTimeSpan = sessions.Lifetime);

        services.AddScoped<UserSignIn>();
        services.AddLocalization(options => options.ResourcesPath = "Resources");
        services.Configure<RequestLocalizationOptions>(ConfigurePageLanguages);

        // Write non-ASCII text (Turkish ş, ğ, ı...) as it is instead of escaping it. This applies to the HTML,
        // JavaScript and URL encoders; each still escapes its own special characters (HTML: < > & " '), so
        // XSS protection is unchanged.
        services.Configure<WebEncoderOptions>(options => options.TextEncoderSettings = new TextEncoderSettings(UnicodeRanges.All));
        services.AddRazorPages();
    }

    /// <summary>
    /// Page texts in Turkish (default) or English, by the browser's Accept-Language. Only the UI culture
    /// changes: formatting stays invariant everywhere, so e.g. Turkish casing rules ("i" → "İ") can never
    /// affect token, API or database code.
    /// </summary>
    public static void ConfigurePageLanguages(RequestLocalizationOptions options)
    {
        options.DefaultRequestCulture = new RequestCulture(CultureInfo.InvariantCulture, new CultureInfo("tr"));
        options.SupportedCultures = [CultureInfo.InvariantCulture];
        options.SupportedUICultures = [new CultureInfo("tr"), new CultureInfo("en")];
    }

    public static void UseLoginPageLanguages(this IApplicationBuilder app) => app.UseRequestLocalization();
}
