using Auth.Client;
using AuthService.Tests.Hosting;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.TestHost;
using SampleApp;

namespace EndToEnd.Tests;

/// <summary>
/// The real SampleApp (its Program.cs and appsettings.json) running in memory against the in-memory AuthService.
/// Its client secret comes from a file, the way a mounted Podman secret reaches it in a container.
/// </summary>
// The type argument only points at SampleApp's assembly. Not Program: AuthService has a Program too.
internal sealed class SampleAppHost : WebApplicationFactory<Note>
{
    private readonly AuthServiceApp _authService;
    private readonly string _secretFile = Path.GetTempFileName();

    public SampleAppHost(AuthServiceApp authService, string clientSecret)
    {
        _authService = authService;

        // With the trailing newline that `echo secret | podman secret create ...` leaves in the file.
        File.WriteAllText(_secretFile, clientSecret + "\n");
    }

    protected override void ConfigureWebHost(IWebHostBuilder builder)
    {
        builder.UseEnvironment("Testing");
        builder.UseSetting("Kale:Authority", "http://localhost/");
        builder.UseSetting("Kale:ClientSecretFile", _secretFile);
        builder.UseSetting("Kale:AllowInsecureHttpDevOnly", "true");

        builder.ConfigureTestServices(services =>
        {
            // Program.cs builds the options from configuration. Only the way to AuthService changes here:
            // the in-memory server instead of the network. Changing the registered instance after AddKaleAuth
            // validated it works because the handler is read later, when JwtBearer's options and
            // AuthServiceConnection are first resolved.
            var options = (KaleAuthOptions)services.Single(d => d.ServiceType == typeof(KaleAuthOptions)).ImplementationInstance!;
            options.BackchannelHandler = _authService.Server.CreateHandler();
        });
    }

    public override async ValueTask DisposeAsync()
    {
        await base.DisposeAsync();
        File.Delete(_secretFile);
    }
}
