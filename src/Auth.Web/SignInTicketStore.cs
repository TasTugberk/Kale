using System.Buffers.Text;
using System.Security.Cryptography;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Authentication.Cookies;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.Extensions.Caching.Distributed;

namespace Auth.Web;

/// <summary>
/// Keeps each sign-in (the user's claims and Kale's tokens) on the server, so the cookie carries only a random key.
/// </summary>
/// <remarks>
/// In the cookie itself the tokens would make it about 7 KB, sent with every request (images and scripts too),
/// split over two cookies and close to the 8 KB per header line many proxies allow (nginx's default). Here, a
/// renewal is saved when its response starts (the cookie handler calls RenewAsync then), even if the browser has
/// gone away by then, instead of only when the browser stores a new cookie. Between Kale's answer and the response
/// starting, TokenRenewal shares the new tokens for 30 seconds with requests that bring the replaced refresh token.
/// So a request that takes longer than that before its response starts can still lose the newest refresh token
/// (the next request would bring the replaced one, which Kale then refuses).
/// The stored value is encrypted with Data Protection, like the cookie, so a shared cache never holds tokens in
/// the clear. The cache is the application's IDistributedCache: in memory unless the application registers a
/// shared one, which it needs with several instances (and then a shared Data Protection key ring too).
/// </remarks>
internal sealed class SignInTicketStore(IDistributedCache cache, IDataProtectionProvider dataProtection, TimeProvider clock) : ITicketStore
{
    private const string KeyPrefix = "kale-signin:";

    private readonly IDataProtector _protector = dataProtection.CreateProtector("Kale.Auth.Web.SignInTicketStore");

    public async Task<string> StoreAsync(AuthenticationTicket ticket)
    {
        var key = KeyPrefix + Base64Url.EncodeToString(RandomNumberGenerator.GetBytes(32));
        await RenewAsync(key, ticket);
        return key;
    }

    public async Task RenewAsync(string key, AuthenticationTicket ticket)
    {
        // Relative to now: the cache's clock and this application's need not agree (tests move this one).
        var lifetime = (ticket.Properties.ExpiresUtc ?? clock.GetUtcNow() + KaleSignInSetup.SignInLifetime) - clock.GetUtcNow();
        if (lifetime <= TimeSpan.Zero)
        {
            await RemoveAsync(key);
            return;
        }

        var value = _protector.Protect(TicketSerializer.Default.Serialize(ticket));

        // Not tied to the request: a renewed refresh token must be saved even if the browser has gone away, or the
        // next request would bring the replaced one, which Kale refuses after 30 seconds.
        await cache.SetAsync(key, value, new DistributedCacheEntryOptions { AbsoluteExpirationRelativeToNow = lifetime }, CancellationToken.None);
    }

    public async Task<AuthenticationTicket?> RetrieveAsync(string key)
    {
        var value = await cache.GetAsync(key);
        if (value is null)
        {
            return null;
        }

        try
        {
            return TicketSerializer.Default.Deserialize(_protector.Unprotect(value));
        }
        catch (CryptographicException)
        {
            // Not ours, or encrypted with a key this application no longer has: the user signs in again.
            return null;
        }
    }

    public Task RemoveAsync(string key) => cache.RemoveAsync(key);
}
