using AuthService.Persistence;
using AuthService.Tests.Persistence;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace AuthService.Tests.Hosting;

/// <summary>Server keys and Data Protection's key ring, as the running app stores them in PostgreSQL.</summary>
[Collection(UsesPostgres.Name)]
public sealed class ServerKeyStorageTests(PostgresFixture postgres)
{
    [Fact]
    public async Task Data_protection_keys_are_stored_in_the_database()
    {
        await using var app = await AuthServiceApp.StartAsync(postgres);
        using var client = app.CreateClient();
        await Jwks.KeyIds(client);

        // Not only working, but stored where every instance reads it (not in a local folder of one machine).
        (await DataProtectionXml(app)).ShouldNotBeEmpty();
    }

    [Fact]
    public async Task A_second_instance_uses_the_keys_the_first_one_created()
    {
        await using var first = await AuthServiceApp.StartAsync(postgres);
        using var firstClient = first.CreateClient();
        var firstKeys = await Jwks.KeyIds(firstClient);

        await using var second = new AuthServiceApp(first.ConnectionString);
        using var secondClient = second.CreateClient();
        var secondKeys = await Jwks.KeyIds(secondClient);

        secondKeys.ShouldBe(firstKeys);
    }

    [Fact]
    public async Task With_a_certificate_the_data_protection_key_ring_is_stored_encrypted()
    {
        using var certificate = TestCertificates.WriteSelfSigned();
        await using var app = await AuthServiceApp.StartAsync(
            postgres,
            ("DataProtection:CertificatePath", certificate.CertificatePath),
            ("DataProtection:PrivateKeyPath", certificate.PrivateKeyPath));
        using var client = app.CreateClient();
        await Jwks.KeyIds(client);

        var xml = await DataProtectionXml(app);

        xml.ShouldNotBeEmpty();
        xml.ShouldAllBe(entry => entry.Contains("EncryptedData", StringComparison.Ordinal));
        xml.ShouldAllBe(entry => !entry.Contains("<value>", StringComparison.Ordinal), "no master key in plain text");
    }

    private static async Task<List<string>> DataProtectionXml(AuthServiceApp app)
    {
        await using var scope = app.Services.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<AuthDbContext>();
        return await db.DataProtectionKeys.Select(k => k.Xml!).ToListAsync();
    }
}
