using System.Globalization;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Authentication.Cookies;

namespace AuthService.SignIn;

/// <summary>
/// When the user last signed in (the password, and the code when they use two-step sign-in), kept in the kale.signin
/// cookie for max_age and the ID token's auth_time; and how (<see cref="MethodsOf"/>), for the ID token's amr.
/// Not the cookie's issue time (<see cref="AuthenticationProperties.IssuedUtc"/>): Identity's security stamp check
/// renews the cookie every 30 minutes and sets that to "now", which would make an old login look fresh. Kept in the
/// cookie's properties rather than as a claim because both renewals keep the properties as they are: the stamp check
/// replaces only the claims, and <c>RefreshSignInAsync</c> (after a password change) copies the old properties.
/// </summary>
public static class SignInTime
{
    private const string Item = "kale.signed_in_at";
    private const string MethodsItem = "kale.signed_in_with";

    /// <summary>
    /// The cookie's OnSigningIn event: a cookie written by a new login gets the time now, and how the user signed in.
    /// One that already has a time (<c>RefreshSignInAsync</c> reuses the old cookie's properties) keeps both, since
    /// the user didn't sign in again.
    /// </summary>
    public static Task RememberAsync(CookieSigningInContext context)
    {
        if (!context.Properties.Items.ContainsKey(Item))
        {
            var now = context.HttpContext.RequestServices.GetRequiredService<TimeProvider>().GetUtcNow();
            context.Properties.Items[Item] = now.ToUnixTimeSeconds().ToString(CultureInfo.InvariantCulture);

            // Identity's SignInManager marks a sign-in completed with a second factor with the claim amr=mfa. The
            // claim itself doesn't last: the stamp check rebuilds the claims without it.
            var withCode = context.Principal?.HasClaim("amr", "mfa") == true;
            context.Properties.Items[MethodsItem] = withCode ? "pwd otp" : "pwd";
        }
        return Task.CompletedTask;
    }

    /// <summary>
    /// How the user signed in, as RFC 8176 names it: "pwd", plus "otp" when they also entered a code (from their
    /// authenticator app, or a recovery code). Empty for a cookie written before this was kept.
    /// </summary>
    public static IReadOnlyList<string> MethodsOf(AuthenticationProperties? cookie) =>
        cookie is not null && cookie.Items.TryGetValue(MethodsItem, out var value) && value is not null
            ? value.Split(' ')
            : [];

    /// <summary>The login time in the cookie's properties, or null for a cookie without one.</summary>
    public static DateTimeOffset? Of(AuthenticationProperties? cookie) =>
        cookie is not null
        && cookie.Items.TryGetValue(Item, out var value)
        && long.TryParse(value, NumberStyles.None, CultureInfo.InvariantCulture, out var seconds)
            ? DateTimeOffset.FromUnixTimeSeconds(seconds)
            : null;
}
