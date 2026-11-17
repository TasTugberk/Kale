using System.Globalization;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Authentication.Cookies;

namespace AuthService.SignIn;

/// <summary>
/// When the user last entered their password, kept in the kale.signin cookie for max_age and the ID token's auth_time.
/// Not the cookie's issue time (<see cref="AuthenticationProperties.IssuedUtc"/>): Identity's security stamp check
/// renews the cookie every 30 minutes and sets that to "now", which would make an old login look fresh. Kept in the
/// cookie's properties rather than as a claim because both renewals keep the properties as they are: the stamp check
/// replaces only the claims, and <c>RefreshSignInAsync</c> (after a password change) copies the old properties.
/// </summary>
public static class SignInTime
{
    private const string Item = "kale.signed_in_at";

    /// <summary>
    /// The cookie's OnSigningIn event: a cookie written by a new login gets the time now. One that already has a time
    /// (<c>RefreshSignInAsync</c> reuses the old cookie's properties) keeps it, since no password was entered again.
    /// </summary>
    public static Task RememberAsync(CookieSigningInContext context)
    {
        if (!context.Properties.Items.ContainsKey(Item))
        {
            var now = context.HttpContext.RequestServices.GetRequiredService<TimeProvider>().GetUtcNow();
            context.Properties.Items[Item] = now.ToUnixTimeSeconds().ToString(CultureInfo.InvariantCulture);
        }
        return Task.CompletedTask;
    }

    /// <summary>The login time in the cookie's properties, or null for a cookie without one.</summary>
    public static DateTimeOffset? Of(AuthenticationProperties? cookie) =>
        cookie is not null
        && cookie.Items.TryGetValue(Item, out var value)
        && long.TryParse(value, NumberStyles.None, CultureInfo.InvariantCulture, out var seconds)
            ? DateTimeOffset.FromUnixTimeSeconds(seconds)
            : null;
}
