using System.Threading.Channels;
using Auth.Contracts.V1;

namespace AuthService.Invalidation;

/// <summary>
/// The invalidation streams connected to this AuthService instance. <see cref="InvalidationNotificationListener"/>
/// delivers each event to the streams of its application; each stream sends its own queue to its client.
/// Subscribing is refused while the listener isn't listening, and losing it ends every stream: either way the
/// client reconnects later and clears its caches, instead of believing it gets every event when it doesn't.
/// </summary>
public sealed class InvalidationSubscribers
{
    /// <summary>
    /// Events a stream may have waiting before it counts as fallen behind. Small enough to bound memory for a dead
    /// or slow client, large enough for a burst: deleting a role stops all its sessions in one save.
    /// </summary>
    public const int QueueCapacity = 1000;

    // One lock for everything: adding, delivering and ending are all quick (no waiting inside), and it makes "end
    // every stream and stop accepting new ones" a single step that no subscription can slip past.
    private readonly Lock _lock = new();

    // A plain list: an instance has one stream per connected client app instance, so a few dozen at most.
    private readonly List<InvalidationSubscription> _subscriptions = [];
    private bool _listening;

    public int Count
    {
        get
        {
            lock (_lock)
            {
                return _subscriptions.Count;
            }
        }
    }

    /// <summary>Whether streams can start now (see <see cref="TryAdd"/>).</summary>
    public bool IsListening
    {
        get
        {
            lock (_lock)
            {
                return _listening;
            }
        }
    }

    /// <summary>A new stream for the application; null while this instance isn't listening for events.</summary>
    public InvalidationSubscription? TryAdd(Guid applicationId)
    {
        lock (_lock)
        {
            if (!_listening)
            {
                return null;
            }

            var subscription = new InvalidationSubscription(applicationId);
            _subscriptions.Add(subscription);
            return subscription;
        }
    }

    public void Remove(InvalidationSubscription subscription)
    {
        lock (_lock)
        {
            _subscriptions.Remove(subscription);
        }
    }

    /// <summary>Queues the event for every stream of the application, ending any stream whose queue is full.</summary>
    public void Deliver(Guid applicationId, InvalidationEvent invalidation)
    {
        lock (_lock)
        {
            // Backwards, so ending (and removing) a stream doesn't skip the next one.
            for (var i = _subscriptions.Count - 1; i >= 0; i--)
            {
                var subscription = _subscriptions[i];
                if (subscription.ApplicationId == applicationId && !subscription.TryQueue(invalidation))
                {
                    subscription.End("This client fell behind: too many events not yet read.");
                    _subscriptions.RemoveAt(i);
                }
            }
        }
    }

    /// <summary>From now on every event saved reaches the streams: subscribing is allowed.</summary>
    public void StartedListening()
    {
        lock (_lock)
        {
            _listening = true;
        }
    }

    /// <summary>Events may be missed from now on: ends every stream and refuses new ones until listening again.</summary>
    public void StoppedListening(string reason)
    {
        lock (_lock)
        {
            _listening = false;
            foreach (var subscription in _subscriptions)
            {
                subscription.End(reason);
            }

            _subscriptions.Clear();
        }
    }
}

/// <summary>One client's stream: the events waiting for it, and why it ended.</summary>
public sealed class InvalidationSubscription
{
    private readonly Channel<InvalidationEvent> _queue = Channel.CreateBounded<InvalidationEvent>(
        new BoundedChannelOptions(InvalidationSubscribers.QueueCapacity) { SingleReader = true });

    internal InvalidationSubscription(Guid applicationId)
    {
        ApplicationId = applicationId;
    }

    public Guid ApplicationId { get; }

    /// <summary>Completes (after the events still queued) when the stream must end; see <see cref="EndReason"/>.</summary>
    public ChannelReader<InvalidationEvent> Events => _queue.Reader;

    /// <summary>Why the stream was ended; null while it's open.</summary>
    public string? EndReason { get; private set; }

    // False when the queue is full (the default full mode makes TryWrite refuse rather than wait or drop).
    internal bool TryQueue(InvalidationEvent invalidation) => _queue.Writer.TryWrite(invalidation);

    internal void End(string reason)
    {
        EndReason = reason;
        _queue.Writer.TryComplete();
    }
}
