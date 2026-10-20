using Auth.Client;
using AuthService.Tests.Hosting;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Time.Testing;

namespace EndToEnd.Tests;

/// <summary>Billing's operations, as a client app would declare them.</summary>
public enum BillingOperations
{
    InvoiceRead,
    [Implies(InvoiceRead)] InvoiceManage,
}

/// <summary>Another enum, to check that a wrong one is caught at startup.</summary>
public enum UnrelatedOperations
{
    Something,
}

/// <summary>
/// A client app ("billing") using the Auth.Client package against the in-memory AuthService, with its own
/// clock for the package's caches.
/// </summary>
internal sealed class ClientApp : IAsyncDisposable
{
    private readonly WebApplication _app;

    private ClientApp(WebApplication app, FakeTimeProvider clock)
    {
        _app = app;
        Clock = clock;
        Http = app.GetTestClient();
    }

    public HttpClient Http { get; }

    public FakeTimeProvider Clock { get; }

    public IServiceProvider Services => _app.Services;

    /// <param name="watchBackchannel">Wraps the handler that reaches AuthService, e.g. to count or fail calls.</param>
    public static async Task<ClientApp> StartAsync(
        AuthServiceApp authService,
        string clientSecret,
        Action<WebApplication>? addEndpoints = null,
        Func<HttpMessageHandler, HttpMessageHandler>? watchBackchannel = null,
        string environment = "Testing")
    {
        var clock = new FakeTimeProvider(DateTimeOffset.UtcNow);
        var builder = WebApplication.CreateBuilder(new WebApplicationOptions { EnvironmentName = environment });
        builder.WebHost.UseTestServer();
        builder.Services.AddSingleton<TimeProvider>(clock);
        builder.Services.AddKaleAuth<BillingOperations>(options =>
        {
            options.Authority = new Uri("http://localhost/");
            options.ApplicationKey = "billing";
            options.ClientSecret = clientSecret;
            options.AllowInsecureHttpDevOnly = true;

            // Reach the in-memory AuthService instead of the network.
            var handler = authService.Server.CreateHandler();
            options.BackchannelHandler = watchBackchannel?.Invoke(handler) ?? handler;
        });

        var app = builder.Build();
        app.UseAuthentication();
        app.UseAuthorization();
        app.MapGet("/invoices", [Auth.Client.Authorize(BillingOperations.InvoiceRead)] () => "invoices");
        app.MapPost("/invoices", [Auth.Client.Authorize(BillingOperations.InvoiceManage)] () => "created");
        addEndpoints?.Invoke(app);

        try
        {
            await app.StartAsync();
        }
        catch
        {
            // Tests that expect startup to fail would otherwise leave the app (and its connections) behind.
            await app.DisposeAsync();
            throw;
        }

        return new ClientApp(app, clock);
    }

    public async ValueTask DisposeAsync()
    {
        Http.Dispose();
        await _app.DisposeAsync();
    }
}
