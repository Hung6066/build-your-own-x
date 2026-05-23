namespace Hope.Agent.Application.Eventing;

public interface IEventPublisher
{
    Task PublishAsync(string topic, string key, string payloadJson, CancellationToken ct);
}

public sealed record EventEnvelope(string Topic, string Key, string PayloadJson, IReadOnlyDictionary<string, string> Headers);

public interface IEventConsumer
{
    /// <summary>
    /// Continuously stream events from the given topics. Implementations must commit offsets after each yielded message.
    /// </summary>
    IAsyncEnumerable<EventEnvelope> ConsumeAsync(IReadOnlyCollection<string> topics, string groupId, CancellationToken ct);
}
