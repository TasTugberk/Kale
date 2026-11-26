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

    public static void AddUserSignIn(this IServiceCollection services, IConfiguration configuration, IHostEnvironment environment)
    {
        services.AddSingleton(new SignInSettings(configuration.GetValue("SignIn:RequireTwoFactor", defaultValue: false)));
        services.AddSingleton(SignInAttemptSettings.From(configuration));
        services.AddSingleton<SignInAttemptLimiter>();

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
            .AddSignInManager()

            // Two-step sign-in with an authenticator app (TOTP) only; no codes by SMS or e-mail. Identity keeps the
            // key and the recovery codes in user_tokens.
            .AddTokenProvider<AuthenticatorTokenProvider<User>>(TokenOptions.DefaultAuthenticatorProvider);

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

            // Identity's own event (the security stamp check, OnValidatePrincipal) stays: only this one is added.
            cookie.Events.OnSigningIn = SignInTime.RememberAsync;
        });

        // Signed in to AuthService for as long as a session lasts (Sessions:Lifetime), without extending it.
        services.AddOptions<CookieAuthenticationOptions>(IdentityConstants.ApplicationScheme)
            .Configure<SessionSettings>((cookie, sessions) => cookie.ExpireTimeSpan = sessions.Lifetime);

        // The page that asks for the code after the password: Identity's own short-lived cookie (5 minutes) holds who
        // entered the password until then.
        services.Configure<CookieAuthenticationOptions>(IdentityConstants.TwoFactorUserIdScheme, cookie =>
        {
            cookie.Cookie.Name = "kale.two_factor";
            cookie.Cookie.SecurePolicy = environment.IsDevelopment() || environment.IsEnvironment("Testing")
                ? CookieSecurePolicy.SameAsRequest
                : CookieSecurePolicy.Always;
        });

        services.AddScoped<UserSignIn>();
        services.AddScoped<Tokens.TestTokens>();
        if (environment.IsDevelopment() || environment.IsEnvironment("Testing"))
        {
            // A local stack set up before test tokens existed: its admin UI client gets the permission too.
            services.AddHostedService<Tokens.AdminUiTestTokenAccess>();
        }
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

/// <param name="RequireTwoFactor">
/// SignIn:RequireTwoFactor: a user without two-step sign-in sets it up after their password, before any application
/// gets a code from them.
/// </param>
public sealed record SignInSettings(bool RequireTwoFactor);
