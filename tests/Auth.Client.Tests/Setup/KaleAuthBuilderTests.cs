using Auth.Client.Tests.Authorization;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;

namespace Auth.Client.Tests.Setup;

/// <summary>
/// builder.AddKaleAuth&lt;TOperations&gt;(): everything comes from the "Kale" configuration section (appsettings,
/// user secrets, environment), with secrets read from files when the *File settings point at them.
/// </summary>
public sealed class KaleAuthBuilderTests : IDisposable
{
    private readonly string _directory = Directory.CreateTempSubdirectory("kale-auth-builder-").FullName;

    public void Dispose() => Directory.Delete(_directory, recursive: true);

    [Fact]
    public void Reads_every_setting_from_the_Kale_section()
    {
        var options = Build(new()
        {
            ["Kale:Authority"] = "http://auth.local:8080/",
            ["Kale:ApplicationKey"] = "billing",
            ["Kale:ClientSecret"] = "secret",
            ["Kale:GrpcAddress"] = "http://auth.local:8081/",
            ["Kale:RabbitMqUri"] = "amqp://kale@rabbitmq.local/",
            ["Kale:RabbitMqPassword"] = "rabbit",
            ["Kale:AllowInsecureHttpDevOnly"] = "true",
        });

        options.Authority.ShouldBe(new Uri("http://auth.local:8080/"));
        options.ApplicationKey.ShouldBe("billing");
        options.ClientSecret.ShouldBe("secret");
        options.GrpcAddress.ShouldBe(new Uri("http://auth.local:8081/"));
        options.RabbitMqUri.ShouldBe(new Uri("amqp://kale@rabbitmq.local/"));
        options.RabbitMqPassword.ShouldBe("rabbit");
        options.AllowInsecureHttpDevOnly.ShouldBeTrue();
    }

    [Fact]
    public void Reads_the_secrets_from_files_the_way_mounted_Podman_secrets_arrive()
    {
        var options = Build(new()
        {
            ["Kale:Authority"] = "https://auth.local/",
            ["Kale:ApplicationKey"] = "billing",
            ["Kale:ClientSecretFile"] = SecretFile("client-secret", "from-file\n"),
            ["Kale:RabbitMqUri"] = "amqps://kale@rabbitmq.local/",
            ["Kale:RabbitMqPasswordFile"] = SecretFile("rabbitmq-password", "rabbit-from-file\n"),
        });

        options.ClientSecret.ShouldBe("from-file", "without the newline `podman secret create` leaves");
        options.RabbitMqPassword.ShouldBe("rabbit-from-file");
    }

    [Fact]
    public void Code_can_still_change_the_options_after_the_configuration()
    {
        var options = Build(
            new() { ["Kale:Authority"] = "https://auth.local/", ["Kale:ApplicationKey"] = "billing", ["Kale:ClientSecret"] = "secret" },
            change => change.GrpcAddress = new Uri("https://grpc.auth.local/"));

        options.GrpcAddress.ShouldBe(new Uri("https://grpc.auth.local/"));
    }

    [Fact]
    public void Without_settings_it_says_which_ones_and_how_to_get_them_locally()
    {
        var error = Should.Throw<InvalidOperationException>(() => Build(new()));

        error.Message.ShouldContain("Kale:Authority");
        error.Message.ShouldContain("scripts/register-app.sh");
    }

    [Theory]
    [InlineData("Kale:ApplicationKey")]
    [InlineData("Kale:ClientSecret")]
    public void Any_one_missing_setting_gets_the_same_message(string missing)
    {
        var settings = new Dictionary<string, string?>
        {
            ["Kale:Authority"] = "https://auth.local/",
            ["Kale:ApplicationKey"] = "billing",
            ["Kale:ClientSecret"] = "secret",
        };
        settings.Remove(missing);

        var error = Should.Throw<InvalidOperationException>(() => Build(settings));

        error.Message.ShouldContain("Kale isn't configured");
    }

    [Fact]
    public void An_empty_secret_file_is_refused_by_its_setting_name()
    {
        // E.g. a Podman secret created from an empty value: say so, instead of "set Kale:ClientSecret".
        var error = Should.Throw<InvalidOperationException>(() => Build(new()
        {
            ["Kale:Authority"] = "https://auth.local/",
            ["Kale:ApplicationKey"] = "billing",
            ["Kale:ClientSecretFile"] = SecretFile("empty", "\n"),
        }));

        error.Message.ShouldContain("Kale:ClientSecretFile");
        error.Message.ShouldContain("is empty");
    }

    private static KaleAuthOptions Build(Dictionary<string, string?> settings, Action<KaleAuthOptions>? change = null)
    {
        var builder = Host.CreateEmptyApplicationBuilder(new HostApplicationBuilderSettings());
        builder.Configuration.AddInMemoryCollection(settings);

        builder.AddKaleAuth<BillingOperations>(change);

        using var services = builder.Services.BuildServiceProvider();
        return services.GetRequiredService<KaleAuthOptions>();
    }

    private string SecretFile(string name, string content)
    {
        var path = Path.Combine(_directory, name);
        File.WriteAllText(path, content);
        return path;
    }
}
