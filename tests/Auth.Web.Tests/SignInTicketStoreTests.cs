using System.Security.Claims;
using System.Text;
using Auth.Web;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.Extensions.Caching.Distributed;
using Microsoft.Extensions.Caching.Memory;
using Microsoft.Extensions.Options;

namespace Auth.Web.Tests;

/// <summary>The sign-in's tokens stay on the server, encrypted; the cookie carries only a key.</summary>
public sealed class SignInTicketStoreTests
{
    private readonly RecordingCache _cache = new();
    private readonly SignInTicketStore _store;

    public SignInTicketStoreTests() => _store = new SignInTicketStore(_cache, new EphemeralDataProtectionProvider(), TimeProvider.System);

    [Fact]
    public async Task A_stored_sign_in_comes_back_by_its_key()
    {
        var key = await _store.StoreAsync(Ticket("access-1"));

        var ticket = await _store.RetrieveAsync(key);

        ticket!.Principal.FindFirst("sid")!.Value.ShouldBe("session-1");
        ticket.Properties.GetTokenValue("access_token").ShouldBe("access-1");
    }

    [Fact]
    public async Task Keys_are_random_and_the_tokens_are_stored_encrypted()
    {
        var first = await _store.StoreAsync(Ticket("secret-access-token"));
        var second = await _store.StoreAsync(Ticket("secret-access-token"));

        first.ShouldNotBe(second);
        Encoding.UTF8.GetString(_cache.Stored[first]).ShouldNotContain("secret-access-token");
    }

    [Fact]
    public async Task A_renewed_sign_in_replaces_the_stored_one_and_a_removed_one_is_gone()
    {
        var key = await _store.StoreAsync(Ticket("access-1"));

        await _store.RenewAsync(key, Ticket("access-2"));
        (await _store.RetrieveAsync(key))!.Properties.GetTokenValue("access_token").ShouldBe("access-2");
        await _store.RemoveAsync(key);

        (await _store.RetrieveAsync(key)).ShouldBeNull();
    }

    [Fact]
    public async Task The_stored_sign_in_expires_with_the_cookie()
    {
        var expires = DateTimeOffset.UtcNow.AddHours(8);

        var key = await _store.StoreAsync(Ticket("access-1", expires));

        _cache.Lifetimes[key].ShouldBeInRange(TimeSpan.FromHours(8) - TimeSpan.FromMinutes(1), TimeSpan.FromHours(8));
    }

    [Fact]
    public async Task Data_this_application_cannot_decrypt_counts_as_no_sign_in()
    {
        _cache.Stored["kale-signin:forged"] = Encoding.UTF8.GetBytes("not encrypted by us");

        (await _store.RetrieveAsync("kale-signin:forged")).ShouldBeNull();
    }

    private static AuthenticationTicket Ticket(string accessToken, DateTimeOffset? expires = null)
    {
        var principal = new ClaimsPrincipal(new ClaimsIdentity([new Claim("sub", "user-1"), new Claim("sid", "session-1")], "test"));
        var properties = new AuthenticationProperties { ExpiresUtc = expires ?? DateTimeOffset.UtcNow.AddHours(8) };
        properties.StoreTokens([new AuthenticationToken { Name = "access_token", Value = accessToken }]);
        return new AuthenticationTicket(principal, properties, "test");
    }

    /// <summary>A real in-memory cache that also shows what was written and for how long.</summary>
    private sealed class RecordingCache : IDistributedCache
    {
        private readonly MemoryDistributedCache _inner = new(Options.Create(new MemoryDistributedCacheOptions()));

        public Dictionary<string, byte[]> Stored { get; } = [];

        public Dictionary<string, TimeSpan> Lifetimes { get; } = [];

        public byte[]? Get(string key) => Stored.GetValueOrDefault(key);

        public Task<byte[]?> GetAsync(string key, CancellationToken token = default) => Task.FromResult(Get(key));

        public void Set(string key, byte[] value, DistributedCacheEntryOptions options)
        {
            Stored[key] = value;
            Lifetimes[key] = options.AbsoluteExpirationRelativeToNow ?? (options.AbsoluteExpiration!.Value - DateTimeOffset.UtcNow);
            _inner.Set(key, value, options);
        }

        public Task SetAsync(string key, byte[] value, DistributedCacheEntryOptions options, CancellationToken token = default)
        {
            Set(key, value, options);
            return Task.CompletedTask;
        }

        public void Refresh(string key)
        {
        }

        public Task RefreshAsync(string key, CancellationToken token = default) => Task.CompletedTask;

        public void Remove(string key) => Stored.Remove(key);

        public Task RemoveAsync(string key, CancellationToken token = default)
        {
            Remove(key);
            return Task.CompletedTask;
        }
    }
}
