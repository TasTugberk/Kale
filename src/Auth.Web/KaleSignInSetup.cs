using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Authentication.Cookies;
using Microsoft.AspNetCore.Authentication.OpenIdConnect;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.WebUtilities;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Hosting;
using Microsoft.IdentityModel.Protocols.OpenIdConnect;

namespace Auth.Web;

public static class KaleSignInSetup
{
    /// <summary>The configuration section <see cref="AddKaleSignIn"/> reads (the same as Kale.Auth.Client's).</summary>
    public const string ConfigurationSection = "Kale";

    /// <summary>The sign-in cookie's authentication scheme.</summary>
    public const string CookieScheme = "KaleCookie";

    /// <summary>The OpenID Connect scheme that talks to Kale (e.g. to configure its backchannel for a proxy).</summary>
    public const string OpenIdConnectScheme = "KaleOpenIdConnect";

    /// <summary>The default scheme when Kale.Auth.Client's AddKaleAuth is used too: bearer token or cookie.</summary>
    public const string BearerOrCookieScheme = "KaleBearerOrCookie";

    /// <summary>A sign-in lasts no longer than a Kale session (8 hours, from sign-in, however often tokens are renewed).</summary>
    public static readonly TimeSpan SignInLifetime = TimeSpan.FromHours(8);

    // Kale.Auth.Client's JWT bearer scheme (JwtBearerDefaults.AuthenticationScheme), named here to need no reference to it.
    private const string BearerScheme = "Bearer";

    /// <summary>
    /// Signs this web application's users in through Kale (backend for frontend: the browser only holds a cookie).
    /// Every setting comes from the "Kale" configuration section, like Kale.Auth.Client's: Authority,
    /// ApplicationKey, ClientSecret (in a container Kale:ClientSecretFile, a mounted secret) and
    /// AllowInsecureHttpDevOnly. Then <c>app.MapKaleSignIn()</c> adds the sign-in endpoints.
    /// </summary>
    /// <param name="configure">Optional changes in code, after the configuration.</param>
    public static IHostApplicationBuilder AddKaleSignIn(this IHostApplicationBuilder builder, Action<KaleSignInOptions>? configure = null)
    {
        var section = builder.Configuration.GetSection(ConfigurationSection);
        var options = new KaleSignInOptions();
        section.Bind(options);
        options.ClientSecret = ReadSecretFile(section, "ClientSecretFile") ?? options.ClientSecret;
        configure?.Invoke(options);
        var isDevelopmentOrTesting = builder.Environment.IsDevelopment() || builder.Environment.IsEnvironment("Testing");
        options.Validate(isDevelopmentOrTesting);

        var services = builder.Services;
        services.TryAddSingleton(TimeProvider.System);
        services.AddHttpContextAccessor();
        services.AddSingleton<TokenRenewal>();
        services.AddSingleton<SignInCookieEvents>();
        services.AddAuthorization();

        // Sign-ins (with their tokens) are kept on the server: see SignInTicketStore. In memory unless the application
        // registers a shared IDistributedCache (needed with several instances); AddDistributedMemoryCache won't
        // replace one registered before, and one registered after takes precedence.
        services.AddDistributedMemoryCache();
        services.AddSingleton<SignInTicketStore>();
        services.AddOptions<CookieAuthenticationOptions>(CookieScheme)
            .Configure<SignInTicketStore>((cookie, store) => cookie.SessionStore = store);

        services.AddAuthentication()
            .AddCookie(CookieScheme, cookie =>
            {
                // Named after the application: on localhost, cookies of different ports would otherwise overwrite each other.
                cookie.Cookie.Name = $"kale.{options.ApplicationKey}";
                cookie.Cookie.HttpOnly = true;
                cookie.Cookie.SameSite = SameSiteMode.Lax;
                cookie.Cookie.SecurePolicy = isDevelopmentOrTesting ? CookieSecurePolicy.SameAsRequest : CookieSecurePolicy.Always;
                cookie.ExpireTimeSpan = SignInLifetime;
                cookie.SlidingExpiration = false;
                cookie.EventsType = typeof(SignInCookieEvents);
            })
            .AddOpenIdConnect(OpenIdConnectScheme, oidc =>
            {
                oidc.Authority = options.Authority!.AbsoluteUri;
                oidc.ClientId = options.ApplicationKey;
                oidc.ClientSecret = options.ClientSecret;
                oidc.RequireHttpsMetadata = !options.AllowInsecureHttpDevOnly;
                oidc.SignInScheme = CookieScheme;
                oidc.SignOutScheme = CookieScheme;

                // Authorization code + PKCE. The code comes back in the address (as for the admin UI): PKCE and the
                // client secret make an intercepted code useless, and it needs no cross-site form post.
                oidc.ResponseType = OpenIdConnectResponseType.Code;
                oidc.ResponseMode = OpenIdConnectResponseMode.Query;
                oidc.UsePkce = true;
                oidc.Scope.Clear();
                oidc.Scope.Add("openid");
                oidc.Scope.Add("offline_access");

                // Tokens are kept with the sign-in (on the server, SignInTicketStore); SignInCookieEvents renews them.
                oidc.SaveTokens = true;

                // Keep Kale's claim names ("sub", "sid"): Kale.Auth.Client's operation check reads "sid".
                oidc.MapInboundClaims = false;
                oidc.TokenValidationParameters.NameClaimType = "name";

                // A slow Kale must not hold a user's request for the default minute.
                oidc.BackchannelTimeout = TimeSpan.FromSeconds(10);

                oidc.Events.OnRedirectToIdentityProviderForSignOut = context =>
                {
                    if (context.HttpContext.Items[SignInFlow.SignOutIdTokenItem] is string idToken)
                    {
                        context.ProtocolMessage.IdTokenHint = idToken;
                    }

                    return Task.CompletedTask;
                };

                // Kale refused the sign-in (e.g. access_denied: no access to this application) or it failed: back
                // to the page with ?kaleError=<code>, so the front end shows its own message, instead of an error page.
                oidc.Events.OnRemoteFailure = context =>
                {
                    var error = context.Failure?.Data["error"] as string ?? "sign_in_failed";
                    var returnUrl = context.Properties?.RedirectUri;
                    context.Response.Redirect(QueryHelpers.AddQueryString(
                        SignInRules.IsLocalReturnUrl(returnUrl) ? returnUrl! : "/", "kaleError", error));
                    context.HandleResponse();
                    return Task.CompletedTask;
                };
            })
            .AddPolicyScheme(BearerOrCookieScheme, "Kale bearer token or sign-in cookie", policy =>
            {
                // In a project that is also an API (Kale.Auth.Client's AddKaleAuth): a request with a bearer token
                // (another server, a tool) is checked as a token; the browser, which only has the cookie, by the
                // cookie. Either way the user carries "sub" and "sid", so [Authorize(Operation)] checks the same
                // session and operations.
                policy.ForwardDefaultSelector = context =>
                    context.Request.Headers.Authorization.ToString().StartsWith("Bearer ", StringComparison.OrdinalIgnoreCase)
                        ? BearerScheme
                        : CookieScheme;
            });

        // AddKaleAuth makes the bearer token the default. PostConfigure runs after every AddAuthentication call, so
        // the order of AddKaleAuth and AddKaleSignIn doesn't matter.
        services.PostConfigure<AuthenticationOptions>(authentication =>
            authentication.DefaultScheme = authentication.SchemeMap.ContainsKey(BearerScheme) ? BearerOrCookieScheme : CookieScheme);

        return builder;
    }

    // A mounted secret file, without the newline `podman secret create` leaves; null when no file is configured.
    // (The same rule as Kale.Auth.Client's; the packages don't depend on each other.)
    private static string? ReadSecretFile(IConfigurationSection section, string setting)
    {
        var path = section[setting];
        if (string.IsNullOrEmpty(path))
        {
            return null;
        }

        var secret = File.ReadAllText(path).Trim();
        return secret.Length > 0
            ? secret
            : throw new InvalidOperationException($"{ConfigurationSection}:{setting} points at '{path}', which is empty.");
    }
}
