using Confluent.Kafka;
using Hope.Agent.Application.Eventing;

namespace Hope.Agent.Infrastructure.Eventing;

public sealed class KafkaEventPublisher : IEventPublisher, IAsyncDisposable
{
    private readonly IProducer<string, string> _producer;

    public KafkaEventPublisher(KafkaOptions options)
    {
        var config = new ProducerConfig
        {
            BootstrapServers = options.BootstrapServers,
            EnableIdempotence = true,
            Acks = Acks.All,
            CompressionType = CompressionType.Zstd,
            LingerMs = 5,
        };
        _producer = new ProducerBuilder<string, string>(config)
            .SetLogHandler((_, _) => { })
            .Build();
    }

    public async Task PublishAsync(string topic, string key, string payloadJson, CancellationToken ct)
    {
        await _producer.ProduceAsync(topic, new Message<string, string> { Key = key, Value = payloadJson }, ct);
    }

    public ValueTask DisposeAsync()
    {
        _producer.Flush(TimeSpan.FromSeconds(5));
        _producer.Dispose();
        return ValueTask.CompletedTask;
    }
}

public sealed class KafkaOptions
{
    public string BootstrapServers { get; set; } = "localhost:9092";
}
