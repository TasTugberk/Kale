namespace Auth.Contracts;

/// <summary>Response headers of InvalidationStream.Subscribe (invalidation_stream.proto).</summary>
public static class InvalidationStreamHeaders
{
    /// <summary>
    /// Sent once the subscription is in place: every change saved from then on reaches the stream. A refused call
    /// never has it, so a client can tell a subscription from a refusal that some clients also report as headers.
    /// </summary>
    public const string Subscribed = "kale-subscribed";
}
