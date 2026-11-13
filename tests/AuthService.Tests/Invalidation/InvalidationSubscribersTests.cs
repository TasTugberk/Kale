using Auth.Contracts.V1;
using AuthService.Invalidation;

namespace AuthService.Tests.Invalidation;

/// <summary>
/// The streams connected to one AuthService instance: who gets which event, and when a stream must end so its
/// client reconnects and clears its caches.
/// </summary>
public sealed class InvalidationSubscribersTests
{
    private static readonly Guid Billing = Guid.CreateVersion7();
    private static readonly Guid Hr = Guid.CreateVersion7();

    private readonly InvalidationSubscribers _subscribers = new();

    [Fact]
    public void Nobody_can_subscribe_before_the_instance_listens_for_events()
    {
        // Events saved now would never reach the stream, but its client would think it was connected.
        _subscribers.TryAdd(Billing).ShouldBeNull();
    }

    [Fact]
    public void An_event_goes_to_the_subscribers_of_its_application_only()
    {
        _subscribers.StartedListening();
        var billing1 = _subscribers.TryAdd(Billing)!;
        var billing2 = _subscribers.TryAdd(Billing)!;
        var hr = _subscribers.TryAdd(Hr)!;
        var invalidation = Event();

        _subscribers.Deliver(Billing, invalidation);

        billing1.Events.TryRead(out var first).ShouldBeTrue();
        first.ShouldBe(invalidation);
        billing2.Events.TryRead(out var second).ShouldBeTrue();
        second.ShouldBe(invalidation);
        hr.Events.TryRead(out _).ShouldBeFalse();
    }

    [Fact]
    public void A_removed_subscriber_gets_nothing_more()
    {
        _subscribers.StartedListening();
        var subscription = _subscribers.TryAdd(Billing)!;

        _subscribers.Remove(subscription);
        _subscribers.Deliver(Billing, Event());

        subscription.Events.TryRead(out _).ShouldBeFalse();
    }

    [Fact]
    public void A_subscriber_that_falls_behind_is_ended_without_holding_up_the_others()
    {
        _subscribers.StartedListening();
        var slow = _subscribers.TryAdd(Billing)!;
        var reader = _subscribers.TryAdd(Billing)!;

        for (var i = 0; i <= InvalidationSubscribers.QueueCapacity; i++)
        {
            _subscribers.Deliver(Billing, Event());
            reader.Events.TryRead(out _).ShouldBeTrue();
        }

        slow.EndReason.ShouldNotBeNull();
        reader.EndReason.ShouldBeNull();

        // Its client reconnects as a new subscriber; the ended one isn't kept around.
        _subscribers.Count.ShouldBe(1);
    }

    [Fact]
    public void Losing_the_feed_of_events_ends_every_stream_and_refuses_new_ones_until_it_is_back()
    {
        _subscribers.StartedListening();
        var billing = _subscribers.TryAdd(Billing)!;
        var hr = _subscribers.TryAdd(Hr)!;

        _subscribers.StoppedListening("connection lost");

        billing.Events.Completion.IsCompleted.ShouldBeTrue();
        billing.EndReason.ShouldBe("connection lost");
        hr.Events.Completion.IsCompleted.ShouldBeTrue();
        _subscribers.Count.ShouldBe(0);
        _subscribers.TryAdd(Billing).ShouldBeNull();

        _subscribers.StartedListening();
        _subscribers.TryAdd(Billing).ShouldNotBeNull();
    }

    private static InvalidationEvent Event() => new()
    {
        EventId = Guid.CreateVersion7().ToString(),
        SessionStopped = new SessionStopped { SessionId = Guid.CreateVersion7().ToString() },
    };
}
