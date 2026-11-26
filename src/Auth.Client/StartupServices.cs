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
                        $"{endpoint.DisplayName} uses {attribute.OperationsEnum.FullName}, but this application's operations are {catalog.EnumType.FullName}.");
                }
            }
        }
    };
}

/// <summary>
/// Sends the operations enum to AuthService on startup (DESIGN.md registration flow), in the background so a
/// slow or restarting AuthService doesn't block the app; retries with growing waits until it succeeds. Then sends
/// it again every <see cref="Interval"/>: AuthService marks an operation obsolete only once no registration has
/// listed it for a while (Operations:ObsoleteAfter, 30 minutes by default), so this version's operations stay
/// active while it runs, even when another version of the app (rolling deployment, rollback) registers too.
/// </summary>
internal sealed partial class OperationRegistrationService(
    AuthServiceConnection connection, OperationCatalog catalog, TimeProvider clock, ILogger<OperationRegistrationService> logger,
    OperationRegistrationStatus status)
    : BackgroundService
{
    // Well below AuthService's Operations:ObsoleteAfter, so a few failed rounds don't make anything obsolete.
    private static readonly TimeSpan Interval = TimeSpan.FromMinutes(5);

    private static readonly TimeSpan MaxWait = TimeSpan.FromSeconds(30);

    // An AuthService that accepts the call but never answers counts as a failure, so it's logged and retried.
    private static readonly TimeSpan Deadline = TimeSpan.FromSeconds(30);

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        var request = new RegisterOperationsRequest();
        foreach (var declaration in catalog.Declarations)
        {
            request.Operations.Add(new OperationDefinition { Name = declaration.Name, Implies = { declaration.Implies } });
        }

        while (!stoppingToken.IsCancellationRequested)
        {
            var accepted = await RegisterAsync(request, stoppingToken);
            if (!accepted)
            {
                // The enum itself is wrong (e.g. an implication cycle): sending it again can't fix it.
                return;
            }

            await Task.Delay(Interval, clock, stoppingToken);
        }
    }

    /// <summary>Sends the operations, retrying with growing waits until AuthService answers.</summary>
    /// <returns>false when AuthService rejected them (INVALID_ARGUMENT).</returns>
    private async Task<bool> RegisterAsync(RegisterOperationsRequest request, CancellationToken stoppingToken)
    {
        var wait = TimeSpan.FromSeconds(1);
        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                var result = await connection.Registration.RegisterOperationsAsync(
                    request, deadline: DateTime.UtcNow + Deadline, cancellationToken: stoppingToken);
                if (result.Added.Count + result.Reactivated.Count + result.Obsoleted.Count > 0)
                {
                    LogRegistered(logger, result.Added.Count, result.Reactivated.Count, result.Obsoleted.Count, result.Unchanged);
                }
                else
                {
                    // Repeated every five minutes, so not worth an Information line each time.
                    LogRegisteredUnchanged(logger, result.Unchanged);
                }

                status.Registered();
                return true;
            }
            catch (RpcException exception) when (exception.StatusCode == StatusCode.InvalidArgument)
            {
                LogRejected(logger, exception.Status.Detail);
                status.Rejected(exception.Status.Detail);
                return false;
            }
            catch (Exception exception) when (!stoppingToken.IsCancellationRequested)
            {
                LogRetrying(logger, exception, wait);
                await Task.Delay(wait, clock, stoppingToken);
                wait = wait * 2 < MaxWait ? wait * 2 : MaxWait;
            }
        }

        return false;
    }

    [LoggerMessage(Level = LogLevel.Debug, Message = "Operations registered with AuthService again: {Unchanged} unchanged.")]
    private static partial void LogRegisteredUnchanged(ILogger logger, int unchanged);

    [LoggerMessage(Level = LogLevel.Information, Message = "Operations registered with AuthService: {Added} added, {Reactivated} reactivated, {Obsoleted} obsoleted, {Unchanged} unchanged.")]
    private static partial void LogRegistered(ILogger logger, int added, int reactivated, int obsoleted, int unchanged);

    [LoggerMessage(Level = LogLevel.Error, Message = "AuthService rejected this application's operations: {Reason}")]
    private static partial void LogRejected(ILogger logger, string reason);

    [LoggerMessage(Level = LogLevel.Warning, Message = "Couldn't register operations with AuthService; retrying in {Wait}.")]
    private static partial void LogRetrying(ILogger logger, Exception exception, TimeSpan wait);
}
