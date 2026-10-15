using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using AuthService.Persistence;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.AspNetCore.DataProtection.EntityFrameworkCore;
using Microsoft.AspNetCore.DataProtection.KeyManagement;
using Microsoft.AspNetCore.DataProtection.XmlEncryption;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;

namespace AuthService.Tests.Startup;

public sealed class ConfigurationValidationTests
{
    // Not "Development": that environment loads .NET user secrets, where scripts/dev-db.sh stores a real
    // connection string, so results would depend on the machine.
    private const string TestEnvironment = "Testing";

    [Fact]
    public void Startup_fails_when_the_AuthDb_connection_string_is_missing()
    {
        using var baseFactory = new WebApplicationFactory<Program>();
        using var factory = baseFactory.WithWebHostBuilder(host => host.UseEnvironment(TestEnvironment));

        var error = Should.Throw<InvalidOperationException>(() => factory.CreateClient());

        error.Message.ShouldContain("AuthDb");
    }

    [Fact]
    public void App_can_create_AuthDbContext_from_its_services()
    {
        // Creating the context doesn't open a connection, so any well-formed connection string is enough.
        using var baseFactory = new WebApplicationFactory<Program>();
        using var factory = baseFactory.WithWebHostBuilder(host => host
            .UseEnvironment(TestEnvironment)
            .UseSetting("ConnectionStrings:AuthDb", "Host=not-used-by-this-test"));
        using var scope = factory.Services.CreateScope();

        scope.ServiceProvider.GetRequiredService<AuthDbContext>().ShouldNotBeNull();
    }

    [Fact]
    public void App_can_create_the_KeyRing_from_its_services()
    {
        using var baseFactory = new WebApplicationFactory<Program>();
        using var factory = baseFactory.WithWebHostBuilder(host => host
            .UseEnvironment(TestEnvironment)
            .UseSetting("ConnectionStrings:AuthDb", "Host=not-used-by-this-test"));
        using var scope = factory.Services.CreateScope();

        scope.ServiceProvider.GetRequiredService<AuthService.Keys.KeyRing>().ShouldNotBeNull();
    }

    [Fact]
    public void Data_protection_keys_are_kept_in_the_database_under_the_apps_name()
    {
        // Every instance must read the same key ring, or one couldn't decrypt the server keys another created.
        using var factory = AppWith(TestEnvironment);

        var keyManagement = factory.Services.GetRequiredService<IOptions<KeyManagementOptions>>().Value;
        keyManagement.XmlRepository.ShouldBeOfType<EntityFrameworkCoreXmlRepository<AuthDbContext>>();

        var dataProtection = factory.Services.GetRequiredService<IOptions<DataProtectionOptions>>().Value;
        dataProtection.ApplicationDiscriminator.ShouldBe("Kale.AuthService");
    }

    [Fact]
    public void Outside_development_startup_requires_a_certificate_to_encrypt_data_protection_keys()
    {
        using var factory = AppWith("Production");

        var error = Should.Throw<InvalidOperationException>(() => factory.CreateClient());

        error.Message.ShouldContain("DataProtection:CertificatePath");
    }

    [Fact]
    public void With_a_certificate_data_protection_keys_are_encrypted_with_it()
    {
        var (certificatePath, privateKeyPath) = WriteSelfSignedCertificate();
        try
        {
            using var factory = AppWith(
                "Production",
                ("DataProtection:CertificatePath", certificatePath),
                ("DataProtection:PrivateKeyPath", privateKeyPath));

            var keyManagement = factory.Services.GetRequiredService<IOptions<KeyManagementOptions>>().Value;

            keyManagement.XmlEncryptor.ShouldBeOfType<CertificateXmlEncryptor>();
        }
        finally
        {
            // Don't leave a private key behind in the temp folder.
            Directory.Delete(Path.GetDirectoryName(certificatePath)!, recursive: true);
        }
    }

    [Fact]
    public void The_apps_database_model_matches_the_migrations()
    {
        // MigrationTests builds the context outside the app. Here it comes from the app's own services, so
        // anything the app's setup adds to the model (e.g. Identity options such as SchemaVersion, which can
        // add a passkeys table) must also be in a migration.
        using var baseFactory = new WebApplicationFactory<Program>();
        using var factory = baseFactory.WithWebHostBuilder(host => host
            .UseEnvironment(TestEnvironment)
            .UseSetting("ConnectionStrings:AuthDb", "Host=not-used-by-this-test"));
        using var scope = factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AuthDbContext>();

        db.Database.HasPendingModelChanges().ShouldBeFalse(
            "the app's setup changes the EF model compared to the migrations");
    }

    private static WebApplicationFactory<Program> AppWith(string environment, params (string Key, string Value)[] settings) =>
        new WebApplicationFactory<Program>().WithWebHostBuilder(host =>
        {
            host.UseEnvironment(environment);
            host.UseSetting("ConnectionStrings:AuthDb", "Host=not-used-by-this-test");
            foreach (var (key, value) in settings)
            {
                host.UseSetting(key, value);
            }
        });

    private static (string CertificatePath, string PrivateKeyPath) WriteSelfSignedCertificate()
    {
        using var rsa = RSA.Create(2048);
        var request = new CertificateRequest(
            "CN=kale-data-protection-test", rsa, HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1);
        using var certificate = request.CreateSelfSigned(DateTimeOffset.UtcNow.AddDays(-1), DateTimeOffset.UtcNow.AddYears(1));

        var directory = Directory.CreateTempSubdirectory("kale-dp-test-").FullName;
        var certificatePath = Path.Combine(directory, "cert.pem");
        var privateKeyPath = Path.Combine(directory, "key.pem");
        File.WriteAllText(certificatePath, certificate.ExportCertificatePem());
        File.WriteAllText(privateKeyPath, rsa.ExportPkcs8PrivateKeyPem());
        return (certificatePath, privateKeyPath);
    }
}
