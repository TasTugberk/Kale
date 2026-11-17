using Auth.Client;
using AuthService.Invalidation;

namespace AuthService.Admin;

/// <summary>
/// The admin API checks access with Auth.Client's own cache (in-process lookups, <see cref="AdminSessionLookups"/>);
/// this keeps that cache current the way a client app's InvalidationListener does, straight from this instance's
/// <see cref="InvalidationSubscribers"/> instead of calling itself over gRPC. So a stopped admin session or a role
/// that lost auth.Admin is refused right away.
/// </summary>
public sealed partial class AdminInvalidationFeed(
    InvalidationSubscribers subscribers, SessionAccess access, ILogger<AdminInvalidationFeed> logger) : BackgroundService
{
    // In-process, so a retry costs nothing: short, because events are missed until it subscribes again.
    private static readonly TimeSpan RetryDelay = TimeSpan.FromSeconds(1);

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        while (!stoppingToken.IsCancellationRequested)
        {
            // Every application's events, not just auth's: auth's id isn't known before `admin bootstrap`, and the
            // other applications' ids are never in this cache, so evicting them changes nothing.
            // Null while this instance isn't listening (starting, or its listening connection was lost).
            var subscription = subscribers.TryAddForEveryApplication();
            if (subscription is not null)
            {
                await ApplyUntilEndedAsync(subscription, stoppingToken);
            }

            await Task.Delay(RetryDelay, stoppingToken);
        }
    }

    private async Task ApplyUntilEndedAsync(InvalidationSubscription subscription, CancellationToken stoppingToken)
    {
        try
        {
            // Like a client app on (re)connecting: events before this moment may have been missed.
            access.EvictAll();
            access.ReceivingEvents = true;
            while (await subscription.Events.WaitToReadAsync(stoppingToken))
            {
                while (subscription.Events.TryRead(out var invalidation))
                {
                    if (!InvalidationListener.TryApply(invalidation, access))
                    {
                        LogUnreadableEvent(invalidation.EventId);
                    }
                }
            }

            LogFeedEnded(subscription.EndReason);
        }
        finally
        {
            access.ReceivingEvents = false;
            subscribers.Remove(subscription);
        }
    }

    [LoggerMessage(Level = LogLevel.Information, Message = "The admin API's invalidation feed ended ({Reason}); subscribing again.")]
    private partial void LogFeedEnded(string? reason);

    [LoggerMessage(Level = LogLevel.Warning, Message = "Skipped an invalidation event that couldn't be read (event id {EventId}).")]
    private partial void LogUnreadableEvent(string eventId);
}
