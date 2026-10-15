using System.Globalization;
using System.Text.Encodings.Web;
using System.Text.Unicode;
using AuthService.Domain;
using AuthService.Persistence;
using Microsoft.AspNetCore.Identity;
using Microsoft.AspNetCore.Localization;
using Microsoft.Extensions.WebEncoders;

namespace AuthService.SignIn;

/// <summary>Users signing in to AuthService itself: Identity, its cookie, and the TR/EN login pages.</summary>
public static class SignInSetup
{
    public const string LoginPath = "/account/login";

    public static void AddUserSignIn(this IServiceCollection services)
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
            // Signed in to AuthService for as long as a session lasts, without silently extending it.
            cookie.ExpireTimeSpan = TimeSpan.FromHours(8);
            cookie.SlidingExpiration = false;
        });

        services.AddScoped<UserSignIn>();
        services.AddLocalization(options => options.ResourcesPath = "Resources");

        // Write Turkish letters (ş, ğ, ı...) as they are instead of escaping them; HTML's special characters
        // (< > & " ') are still escaped, so this doesn't weaken XSS protection.
        services.Configure<WebEncoderOptions>(options => options.TextEncoderSettings = new TextEncoderSettings(UnicodeRanges.All));
        services.AddRazorPages();
    }

    /// <summary>Turkish first, English second; the browser's Accept-Language picks between them.</summary>
    public static void UseLoginPageLanguages(this IApplicationBuilder app) =>
        app.UseRequestLocalization(new RequestLocalizationOptions
        {
            DefaultRequestCulture = new RequestCulture("tr"),
            SupportedCultures = [new CultureInfo("tr"), new CultureInfo("en")],
            SupportedUICultures = [new CultureInfo("tr"), new CultureInfo("en")],
        });
}
