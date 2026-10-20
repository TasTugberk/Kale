using AuthService.Keys;
using AuthService.Persistence;
using AuthService.Tests.Persistence;
using Microsoft.AspNetCore.Authentication.Cookies;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.AspNetCore.DataProtection.EntityFrameworkCore;
using Microsoft.AspNetCore.DataProtection.KeyManagement;
using Microsoft.AspNetCore.DataProtection.XmlEncryption;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Identity;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using Npgsql;

namespace AuthService.Tests.Startup;

/// <summary>
/// What the app requires at startup, and how its services are wired. The app loads its keys from the database
/// while starting, so these run against a real (migrated) test database.
/// </summary>
[Collection(UsesPostgres.Name)]
public sealed class ConfigurationValidationTests(PostgresFixture postgres) : IAsyncLifetime
{
    // Not "Development": that environment loads .NET user secrets, where scripts/dev-db.sh stores a real
    // connection string, so results would depend on the machine.
    private const string TestEnvironment = "Testing";

    private string _connectionString = null!;

    public async Task InitializeAsync()
    {
        await using var db = postgres.CreateDbContext();
        await db.Database.MigrateAsync();
        _connectionString = postgres.ConnectionString(db.Database.GetDbConnection().Database);
    }

    public Task DisposeAsync() => Task.CompletedTask;

    [Fact]
    public void Startup_fails_when_the_AuthDb_connection_string_is_missing()
    {
        using var baseFactory = new WebApplicationFactory<Program>();
        using var factory = baseFactory.WithWebHostBuilder(host => host.UseEnvironment(TestEnvironment));

        var error = Should.Throw<InvalidOperationException>(() => factory.CreateClient());

        error.Message.ShouldContain("AuthDb");
    }

    [Fact]
    public async Task The_database_password_can_come_from_a_file()
    {
        // In a container it's a mounted Podman secret, never part of the connection string in configuration.
        var withoutPassword = new NpgsqlConnectionStringBuilder(_connectionString);
        var passwordFile = Path.GetTempFileName();
        await File.WriteAllTextAsync(passwordFile, withoutPassword.Password + "\n");
        withoutPassword.Password = null;
        try
        {
            using var factory = new WebApplicationFactory<Program>().WithWebHostBuilder(host =>
            {
                host.UseEnvironment(TestEnvironment);
                host.UseSetting("ConnectionStrings:AuthDb", withoutPassword.ConnectionString);
                host.UseSetting("AuthDb:PasswordFile", passwordFile);
            });

            // Starting at all means the app could read its keys from the database.
            using var scope = factory.Services.CreateScope();
            (await scope.ServiceProvider.GetRequiredService<AuthDbContext>().ServerKeys.AnyAsync()).ShouldBeTrue();
        }
        finally
        {
            File.Delete(passwordFile);
        }
    }

    [Fact]
    public void App_can_create_AuthDbContext_and_KeyRing_from_its_services()
    {
        using var factory = AppWith(TestEnvironment);
        using var scope = factory.Services.CreateScope();

        scope.ServiceProvider.GetRequiredService<AuthDbContext>().ShouldNotBeNull();
        scope.ServiceProvider.GetRequiredService<KeyRing>().ShouldNotBeNull();
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
        using var factory = AppWith("Production", ("Auth:Issuer", "https://auth.kale.test/"));

        var error = Should.Throw<InvalidOperationException>(() => factory.CreateClient());

        error.Message.ShouldContain("DataProtection:CertificatePath");
    }

    [Fact]
    public void With_a_certificate_data_protection_keys_are_encrypted_with_it()
    {
        using var certificate = TestCertificates.WriteSelfSigned();
        using var factory = AppWith("Production", ProductionSettings(certificate));

        var keyManagement = factory.Services.GetRequiredService<IOptions<KeyManagementOptions>>().Value;

        keyManagement.XmlEncryptor.ShouldBeOfType<CertificateXmlEncryptor>();
    }

    [Fact]
    public void Outside_development_startup_requires_an_issuer()
    {
        // Without a fixed issuer, "iss" and the discovery URLs would follow each request's Host header.
        using var certificate = TestCertificates.WriteSelfSigned();
        using var factory = AppWith("Production", ProductionSettings(certificate)[..2]);

        var error = Should.Throw<InvalidOperationException>(() => factory.CreateClient());

        error.Message.ShouldContain("Auth:Issuer");
    }

    [Fact]
    public void Outside_development_startup_requires_a_RabbitMQ_address()
    {
        // Without a broker, invalidation events would pile up in the outbox and client apps would only notice
        // stopped sessions and edited roles when their caches expire.
        using var certificate = TestCertificates.WriteSelfSigned();
        using var factory = AppWith("Production", ProductionSettings(certificate)[..3]);

        var error = Should.Throw<InvalidOperationException>(() => factory.CreateClient());

        error.Message.ShouldContain("RabbitMq:Uri");
    }

    [Fact]
    public void Outside_development_RabbitMQ_must_be_reached_over_TLS()
    {
        using var certificate = TestCertificates.WriteSelfSigned();
        using var factory = AppWith("Production", [.. ProductionSettings(certificate)[..3], ("RabbitMq:Uri", "amqp://localhost:1/")]);

        var error = Should.Throw<InvalidOperationException>(() => factory.CreateClient());

        error.Message.ShouldContain("amqps");
    }

    [Fact]
    public void A_RabbitMQ_address_that_is_not_amqp_stops_startup()
    {
        using var factory = AppWith(TestEnvironment, ("RabbitMq:Uri", "http://localhost:5672/"));

        var error = Should.Throw<InvalidOperationException>(() => factory.CreateClient());

        error.Message.ShouldContain("RabbitMq:Uri");
    }

    [Fact]
    public void The_insecure_http_flag_is_refused_outside_development()
    {
        using var certificate = TestCertificates.WriteSelfSigned();
        using var factory = AppWith(
            "Production", [.. ProductionSettings(certificate), ("OpenIddict:AllowInsecureHttpDevOnly", "true")]);

        var error = Should.Throw<InvalidOperationException>(() => factory.CreateClient());

        error.Message.ShouldContain("OpenIddict:AllowInsecureHttpDevOnly");
    }

    [Theory]
    [InlineData("Http2")]
    [InlineData("http2 ")] // Kestrel reads the value as an enum: any case, spaces around it,
    [InlineData("2")]      // or its number
    public void A_plain_HTTP2_endpoint_needs_the_dev_only_gRPC_flag(string protocols)
    {
        // gRPC without TLS (h2c) is for the local compose network only, and must say so in configuration.
        using var factory = AppWith(TestEnvironment,
            ("Kestrel:Endpoints:GrpcDevOnly:Url", "http://127.0.0.1:0"),
            ("Kestrel:Endpoints:GrpcDevOnly:Protocols", protocols));

        var error = Should.Throw<InvalidOperationException>(() => factory.CreateClient());

        error.Message.ShouldContain("Grpc:AllowInsecureDevOnly");
    }

    [Fact]
    public void HTTP2_for_every_endpoint_by_default_also_needs_the_dev_only_gRPC_flag()
    {
        // Addresses from ASPNETCORE_URLS/--urls don't show up under Kestrel:Endpoints, but Kestrel still applies
        // EndpointDefaults to them.
        using var factory = AppWith(TestEnvironment,
            ("urls", "http://127.0.0.1:0"),
            ("Kestrel:EndpointDefaults:Protocols", "Http2"));

        var error = Should.Throw<InvalidOperationException>(() => factory.CreateClient());

        error.Message.ShouldContain("Grpc:AllowInsecureDevOnly");
    }

    [Fact]
    public void A_plain_HTTP_endpoint_that_takes_HTTP2_from_the_defaults_needs_the_dev_only_gRPC_flag()
    {
        using var factory = AppWith(TestEnvironment,
            ("Kestrel:Endpoints:Grpc:Url", "http://127.0.0.1:0"),
            ("Kestrel:EndpointDefaults:Protocols", "Http2"));

        var error = Should.Throw<InvalidOperationException>(() => factory.CreateClient());

        error.Message.ShouldContain("Grpc:AllowInsecureDevOnly");
    }

    [Fact]
    public void The_dev_only_gRPC_flag_is_refused_outside_development()
    {
        using var certificate = TestCertificates.WriteSelfSigned();
        using var factory = AppWith("Production", [.. ProductionSettings(certificate), ("Grpc:AllowInsecureDevOnly", "true")]);

        var error = Should.Throw<InvalidOperationException>(() => factory.CreateClient());

        error.Message.ShouldContain("Grpc:AllowInsecureDevOnly");
    }

    [Fact]
    public void In_production_the_sign_in_cookie_is_only_sent_over_https()
    {
        using var certificate = TestCertificates.WriteSelfSigned();
        using var factory = AppWith("Production", ProductionSettings(certificate));

        var cookie = factory.Services.GetRequiredService<IOptionsMonitor<CookieAuthenticationOptions>>()
            .Get(IdentityConstants.ApplicationScheme);

        cookie.Cookie.SecurePolicy.ShouldBe(CookieSecurePolicy.Always);
    }

    [Fact]
    public void Formatting_stays_culture_invariant_only_page_texts_follow_the_language()
    {
        using var factory = AppWith(TestEnvironment);

        var languages = factory.Services.GetRequiredService<IOptions<RequestLocalizationOptions>>().Value;

        languages.SupportedCultures!.ShouldBe([System.Globalization.CultureInfo.InvariantCulture]);
        languages.SupportedUICultures!.Select(c => c.Name).ShouldBe(["tr", "en"]);
    }

    [Theory]
    [InlineData("00:00:00")]   // zero
    [InlineData("-01:00:00")]  // negative
    [InlineData("1.00:00:00")] // 1 day: refresh tokens would outlive the keys that encrypt them
    public void Startup_refuses_a_session_lifetime_that_is_not_positive_or_too_long(string lifetime)
    {
        using var factory = AppWith(TestEnvironment, ("Sessions:Lifetime", lifetime));

        var error = Should.Throw<InvalidOperationException>(() => factory.CreateClient());

        error.Message.ShouldContain("Sessions:Lifetime");
    }

    [Fact]
    public void The_session_lifetime_also_sets_the_sign_in_cookie_and_refresh_token_lifetimes()
    {
        using var factory = AppWith(TestEnvironment, ("Sessions:Lifetime", "04:00:00"));

        var cookie = factory.Services.GetRequiredService<IOptionsMonitor<CookieAuthenticationOptions>>()
            .Get(IdentityConstants.ApplicationScheme);
        var server = factory.Services.GetRequiredService<IOptionsMonitor<OpenIddict.Server.OpenIddictServerOptions>>().CurrentValue;

        cookie.ExpireTimeSpan.ShouldBe(TimeSpan.FromHours(4));
        server.RefreshTokenLifetime.ShouldBe(TimeSpan.FromHours(4));
    }

    [Fact]
    public void The_apps_database_model_matches_the_migrations()
    {
        // MigrationTests builds the context outside the app. Here it comes from the app's own services, so
        // anything the app's setup adds to the model (e.g. Identity options such as SchemaVersion, which can
        // add a passkeys table) must also be in a migration.
        using var factory = AppWith(TestEnvironment);
        using var scope = factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AuthDbContext>();

        db.Database.HasPendingModelChanges().ShouldBeFalse(
            "the app's setup changes the EF model compared to the migrations");
    }

    private static (string Key, string Value)[] ProductionSettings(TestCertificate certificate) =>
    [
        ("DataProtection:CertificatePath", certificate.CertificatePath),
        ("DataProtection:PrivateKeyPath", certificate.PrivateKeyPath),
        ("Auth:Issuer", "https://auth.kale.test/"),

        // Nothing listens there; the publisher only logs that it can't connect.
        ("RabbitMq:Uri", "amqps://localhost:1/"),
    ];

    private WebApplicationFactory<Program> AppWith(string environment, params (string Key, string Value)[] settings) =>
        new WebApplicationFactory<Program>().WithWebHostBuilder(host =>
        {
            host.UseEnvironment(environment);
            host.UseSetting("ConnectionStrings:AuthDb", _connectionString);
            foreach (var (key, value) in settings)
            {
                host.UseSetting(key, value);
            }
        });
}
