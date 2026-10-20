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

    private static void Configure(Action<KaleAuthOptions> change) =>
        new ServiceCollection().AddKaleAuth<BillingOperations>(options =>
        {
            options.Authority = new Uri("https://auth.local/");
            options.ApplicationKey = "billing";
            options.ClientSecret = "secret";
            change(options);
        });
}
