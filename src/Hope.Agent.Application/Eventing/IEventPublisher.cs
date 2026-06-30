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

public sealed record OutboxEventWrite(
    Guid? TenantId,
    string Topic,
    string Key,
    string PayloadJson,
    IReadOnlyDictionary<string, string>? Headers = null,
    string? CorrelationId = null,
    string? IdempotencyKey = null,
    int MaxAttempts = 5,
    DateTimeOffset? ScheduledFor = null);

public interface IOutboxStore
{
    Task<Guid> AddAsync(OutboxEventWrite write, CancellationToken ct);
    Task<IReadOnlyList<Hope.Agent.Domain.Eventing.OutboxEvent>> DueAsync(DateTimeOffset now, int take, CancellationToken ct);
    Task MarkPublishingAsync(Guid id, CancellationToken ct);
    Task MarkPublishedAsync(Guid id, CancellationToken ct);
    Task MarkFailedAsync(Guid id, string error, DateTimeOffset nextAttemptAt, CancellationToken ct);
    Task MarkDeadLetterAsync(Guid id, string error, CancellationToken ct);
}

public sealed class OutboxOptions
{
    public const string SectionName = "Outbox";
    public bool Enabled { get; init; } = true;
    public int BatchSize { get; init; } = 100;
    public int PollSeconds { get; init; } = 5;
    public int MaxAttempts { get; init; } = 5;
    public int BaseBackoffSeconds { get; init; } = 2;
    public int MaxBackoffSeconds { get; init; } = 300;
    public int JitterSeconds { get; init; } = 5;
}
