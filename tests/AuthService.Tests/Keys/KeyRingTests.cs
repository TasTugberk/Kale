using System.Security.Cryptography;
using AuthService.Domain;
using AuthService.Keys;
using AuthService.Persistence;
using AuthService.Tests.Persistence;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Time.Testing;
using Microsoft.IdentityModel.Tokens;

namespace AuthService.Tests.Keys;

[Collection(UsesPostgres.Name)]
public sealed class KeyRingTests(PostgresFixture postgres) : IAsyncLifetime
{
    private static readonly DateTimeOffset Start = new(2026, 6, 1, 12, 0, 0, TimeSpan.Zero);

    private readonly FakeTimeProvider _clock = new(Start);

    // One provider for all "instances" in a test: in production they share Data Protection's key ring in PostgreSQL.
    private readonly IDataProtectionProvider _dataProtection = new EphemeralDataProtectionProvider();
    private AuthDbContext _db = null!;

    public async Task InitializeAsync()
    {
        _db = postgres.CreateDbContext(_clock);
        await _db.Database.MigrateAsync();
    }

    public async Task DisposeAsync() => await _db.DisposeAsync();

    [Fact]
    public async Task First_refresh_creates_one_signing_and_one_encryption_key()
    {
        var keys = await NewKeyRing().RefreshAsync();

        (await _db.ServerKeys.CountAsync(k => k.Use == KeyUse.Signing)).ShouldBe(1);
        (await _db.ServerKeys.CountAsync(k => k.Use == KeyUse.Encryption)).ShouldBe(1);
        keys.Signing.All.ShouldBe([keys.Signing.Current]);
        keys.Encryption.All.ShouldBe([keys.Encryption.Current]);
        keys.Signing.Current.KeyId.ShouldNotBe(keys.Encryption.Current.KeyId);
    }

    [Fact]
    public async Task The_current_signing_key_can_sign_and_verify()
    {
        var keys = await NewKeyRing().RefreshAsync();
        var data = "header.payload"u8.ToArray();

        var rsa = keys.Signing.Current.ShouldBeOfType<RsaSecurityKey>().Rsa;
        var signature = rsa.SignData(data, HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1);

        rsa.VerifyData(data, signature, HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1).ShouldBeTrue();
        rsa.KeySize.ShouldBeGreaterThanOrEqualTo(2048);
    }

    [Fact]
    public async Task Private_keys_are_stored_encrypted()
    {
        await NewKeyRing().RefreshAsync();

        var stored = await _db.ServerKeys.AsNoTracking().Select(k => k.ProtectedPrivateKey).ToListAsync();

        stored.ShouldAllBe(value => !value.Contains("PRIVATE KEY", StringComparison.Ordinal));

        // Only Data Protection with the same key ring can read them back; any other key ring can't.
        var otherKeyRing = new EphemeralDataProtectionProvider().CreateProtector("Kale.AuthService.ServerKeys");
        Should.Throw<CryptographicException>(() => otherKeyRing.Unprotect(stored[0]));
    }

    [Fact]
    public async Task Refreshing_again_reuses_the_stored_keys()
    {
        var first = await NewKeyRing().RefreshAsync();

        // Like a restart: a new context and key ring, same database and Data Protection keys.
        await using var restartedDb = postgres.CreateDbContext(DatabaseName, _clock);
        var second = await new KeyRing(restartedDb, _dataProtection, _clock, new KeyRotationOptions()).RefreshAsync();

        second.Signing.Current.KeyId.ShouldBe(first.Signing.Current.KeyId);
        second.Encryption.Current.KeyId.ShouldBe(first.Encryption.Current.KeyId);
        (await _db.ServerKeys.CountAsync()).ShouldBe(2);
    }

    [Fact]
    public async Task Keys_rotate_through_their_lifecycle()
    {
        var day0 = await NewKeyRing().RefreshAsync();
        var firstSigning = day0.Signing.Current.KeyId;
        var firstEncryption = day0.Encryption.Current.KeyId;

        _clock.Advance(TimeSpan.FromDays(30));
        var day30 = await NewKeyRing().RefreshAsync();
        day30.Signing.Current.KeyId.ShouldBe(firstSigning, "the successor is only published on day 30");
        day30.Signing.All.Count.ShouldBe(2);
        day30.Encryption.All.Count.ShouldBe(2);

        _clock.Advance(TimeSpan.FromDays(1));
        var day31 = await NewKeyRing().RefreshAsync();
        day31.Signing.Current.KeyId.ShouldNotBe(firstSigning, "the successor signs from day 31");
        day31.Encryption.Current.KeyId.ShouldNotBe(firstEncryption, "encryption keys rotate the same way");
        day31.Signing.All.Select(k => k.KeyId).ShouldContain(firstSigning, "the old key is still published");
        day31.Signing.All[0].ShouldBe(day31.Signing.Current, "OpenIddict signs with the first key it is given");

        _clock.Advance(TimeSpan.FromDays(1));
        var day32 = await NewKeyRing().RefreshAsync();
        day32.Signing.All.Select(k => k.KeyId).ShouldNotContain(firstSigning, "deleted a day after it stopped signing");
        day32.Encryption.All.Select(k => k.KeyId).ShouldNotContain(firstEncryption);
    }

    [Fact]
    public async Task Instances_starting_at_once_create_only_one_key_of_each_kind()
    {
        var instances = Enumerable.Range(0, 6).Select(async _ =>
        {
            await using var instanceDb = postgres.CreateDbContext(DatabaseName, _clock);
            return await new KeyRing(instanceDb, _dataProtection, _clock, new KeyRotationOptions()).RefreshAsync();
        });
        var results = await Task.WhenAll(instances);

        (await _db.ServerKeys.CountAsync()).ShouldBe(2);
        results.Select(r => r.Signing.Current.KeyId).Distinct().Count().ShouldBe(1);
    }

    private string DatabaseName => _db.Database.GetDbConnection().Database;

    private KeyRing NewKeyRing() => new(_db, _dataProtection, _clock, new KeyRotationOptions());
}
