using System.Globalization;
using Microsoft.AspNetCore.Http;

namespace Auth.Web;

/// <summary>The small decisions of the sign-in, kept apart from ASP.NET Core's plumbing so they can be tested alone.</summary>
internal static class SignInRules
{
    /// <summary>How long before its expiry the access token is renewed, so a call made with it still has time.</summary>
    public static readonly TimeSpan RenewBefore = TimeSpan.FromMinutes(1);

    /// <summary>
    /// Whether the access token must be renewed now. <paramref name="expiresAt"/> is the stored "expires_at" token
    /// value; when it is missing or unreadable the token is renewed, rather than used without knowing.
    /// </summary>
    public static bool NeedsRenewal(string? expiresAt, DateTimeOffset now)
    {
        if (!DateTimeOffset.TryParse(expiresAt, CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind, out var expires))
        {
            return true;
        }

        return expires - now <= RenewBefore;
    }

    /// <summary>
    /// A path on this site ("/notes?page=2"), never an address elsewhere: after sign-in the browser is sent there,
    /// and an open redirect would let another site use this one's sign-in page as a trusted-looking hop.
    /// "//host" and "/\host" are addresses on another host to a browser.
    /// </summary>
    public static bool IsLocalReturnUrl(string? url)
    {
        if (string.IsNullOrEmpty(url) || url[0] != '/')
        {
            return false;
        }

        if (url.Length > 1 && (url[1] == '/' || url[1] == '\\'))
        {
            return false;
        }

        return !url.Any(char.IsControl);
    }

    /// <summary>
    /// Whether a request comes from this site's own pages (as the admin UI's isSameOrigin). Browsers send Origin
    /// with every POST, PUT, PATCH and DELETE; Sec-Fetch-Site covers the rest. Without either, it does not.
    /// This site's origin is the request's own scheme and host: behind a proxy, forward them (ForwardedHeaders),
    /// as the sign-in's return address needs anyway.
    /// </summary>
    public static bool IsSameOrigin(HttpRequest request)
    {
        var origin = request.Headers.Origin.ToString();
        if (origin.Length > 0)
        {
            var ownOrigin = $"{request.Scheme}://{request.Host.Value}";
            return string.Equals(origin, ownOrigin, StringComparison.OrdinalIgnoreCase);
        }

        return request.Headers["Sec-Fetch-Site"] == "same-origin";
    }

    /// <summary>
    /// Whether the browser is opening a page (a link, the address bar, a form), as opposed to a script's fetch.
    /// Only a page can be sent to Kale's sign-in; a fetch gets 401 so the front end can react. Browsers say which
    /// in Sec-Fetch-Mode; without it, a request asking for HTML counts as a page.
    /// </summary>
    public static bool IsPageNavigation(HttpRequest request)
    {
        var mode = request.Headers["Sec-Fetch-Mode"].ToString();
        if (mode.Length > 0)
        {
            return mode == "navigate";
        }

        return request.Headers.Accept.ToString().Contains("text/html", StringComparison.OrdinalIgnoreCase);
    }
}
