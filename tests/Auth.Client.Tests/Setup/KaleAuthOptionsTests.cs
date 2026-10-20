using Auth.Client.Tests.Authorization;
using Microsoft.Extensions.DependencyInjection;

namespace Auth.Client.Tests.Setup;

/// <summary>AddKaleAuth refuses settings that can't work, at the line that configures them.</summary>
public sealed class KaleAuthOptionsTests
{
    [Fact]
    public void Plain_http_to_AuthService_needs_the_dev_only_flag()
    {
        var error = Should.Throw<InvalidOperationException>(() => Configure(options => options.Authority = new Uri("http://auth.local/")));

        error.Message.ShouldContain("https");
    }

    [Fact]
    public void With_the_dev_only_flag_plain_http_is_accepted()
    {
        Configure(options =>
        {
            options.Authority = new Uri("http://auth.local/");
            options.AllowInsecureHttpDevOnly = true;
        });
    }

    [Fact]
    public void A_RabbitMQ_address_must_be_amqp()
    {
        var error = Should.Throw<InvalidOperationException>(() => Configure(options => options.RabbitMqUri = new Uri("http://rabbitmq:5672/")));

        error.Message.ShouldContain("RabbitMqUri");
    }

    [Fact]
    public void A_plain_HTTP_gRPC_address_needs_the_dev_only_flag()
    {
        var error = Should.Throw<InvalidOperationException>(() => Configure(options => options.GrpcAddress = new Uri("http://auth.local:8081/")));

        error.Message.ShouldContain("GrpcAddress");
    }

    [Fact]
    public void A_gRPC_address_must_be_absolute()
    {
        var error = Should.Throw<InvalidOperationException>(() => Configure(options => options.GrpcAddress = new Uri("/grpc", UriKind.Relative)));

        error.Message.ShouldContain("GrpcAddress");
    }

    [Fact]
    public void The_authority_is_required()
    {
        var error = Should.Throw<InvalidOperationException>(() => Configure(options => options.Authority = null));

        error.Message.ShouldContain("Authority");
    }

    [Theory]
    [InlineData("", "secret")]
    [InlineData("billing", " ")]
    public void The_application_key_and_client_secret_are_required(string key, string secret)
    {
        var error = Should.Throw<InvalidOperationException>(() => Configure(options =>
        {
            options.ApplicationKey = key;
            options.ClientSecret = secret;
        }));

        error.Message.ShouldContain("ClientSecret");
    }

    [Fact]
    public void An_authority_with_a_path_but_no_trailing_slash_keeps_its_path()
    {
        // Relative URLs (connect/token) would otherwise replace the last segment: /auth + connect/token = /connect/token.
        var services = Configure(options => options.Authority = new Uri("https://example.com/auth"));

        services.BuildServiceProvider().GetRequiredService<KaleAuthOptions>().Authority.ShouldBe(new Uri("https://example.com/auth/"));
    }

    private static IServiceCollection Configure(Action<KaleAuthOptions> change) =>
        new ServiceCollection().AddKaleAuth<BillingOperations>(options =>
        {
            options.Authority = new Uri("https://auth.local/");
            options.ApplicationKey = "billing";
            options.ClientSecret = "secret";
            change(options);
        });
}
