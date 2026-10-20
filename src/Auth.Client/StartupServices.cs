using Auth.Contracts.V1;
using Grpc.Core;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace Auth.Client;

/// <summary>
/// Checks that every [Authorize(...)] uses this application's operations enum; otherwise the app doesn't start
/// (instead of failing those requests later). A startup filter, because endpoints exist only once the app's
/// pipeline is built, which happens after hosted services have started.
/// </summary>
internal sealed class OperationsEnumCheck(OperationCatalog catalog) : IStartupFilter
{
    public Action<IApplicationBuilder> Configure(Action<IApplicationBuilder> next) => app =>
    {
        next(app);

        var endpoints = app.ApplicationServices.GetService<EndpointDataSource>()?.Endpoints ?? [];
        foreach (var endpoint in endpoints)
        {
            foreach (var attribute in endpoint.Metadata.OfType<AuthorizeAttribute>())
            {
                if (attribute.OperationsEnum != catalog.EnumType)
                {
                    throw new InvalidOperationException(
                        $"{endpoint.DisplayName} uses {attribute.OperationsEnum.Name}, but this application's operations are {catalog.EnumType.Name}.");
                }
            }
        }
    };
}

/// <summary>
/// Sends the operations enum to AuthService on startup (DESIGN.md registration flow), in the background so a
/// slow or restarting AuthService doesn't block the app; retries with growing waits until it succeeds.
/// </summary>
internal sealed partial class OperationRegistrationService(
    AuthServiceConnection connection, OperationCatalog catalog, TimeProvider clock, ILogger<OperationRegistrationService> logger)
    : BackgroundService
{
    private static readonly TimeSpan MaxWait = TimeSpan.FromSeconds(30);

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        var request = new RegisterOperationsRequest();
        foreach (var declaration in catalog.Declarations)
        {
            request.Operations.Add(new OperationDefinition { Name = declaration.Name, Implies = { declaration.Implies } });
        }

        var wait = TimeSpan.FromSeconds(1);
        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                var result = await connection.Registration.RegisterOperationsAsync(request, cancellationToken: stoppingToken);
                LogRegistered(logger, result.Added.Count, result.Reactivated.Count, result.Obsoleted.Count, result.Unchanged);
                return;
            }
            catch (RpcException exception) when (exception.StatusCode == StatusCode.InvalidArgument)
            {
                // The enum itself is wrong (e.g. an implication cycle): retrying can't fix it.
                LogRejected(logger, exception.Status.Detail);
                return;
            }
            catch (Exception exception) when (!stoppingToken.IsCancellationRequested)
            {
                LogRetrying(logger, exception, wait);
                await Task.Delay(wait, clock, stoppingToken);
                wait = TimeSpan.FromTicks(Math.Min(wait.Ticks * 2, MaxWait.Ticks));
            }
        }
    }

    [LoggerMessage(Level = LogLevel.Information, Message = "Operations registered with AuthService: {Added} added, {Reactivated} reactivated, {Obsoleted} obsoleted, {Unchanged} unchanged.")]
    private static partial void LogRegistered(ILogger logger, int added, int reactivated, int obsoleted, int unchanged);

    [LoggerMessage(Level = LogLevel.Error, Message = "AuthService rejected this application's operations: {Reason}")]
    private static partial void LogRejected(ILogger logger, string reason);

    [LoggerMessage(Level = LogLevel.Warning, Message = "Couldn't register operations with AuthService; retrying in {Wait}.")]
    private static partial void LogRetrying(ILogger logger, Exception exception, TimeSpan wait);
}
