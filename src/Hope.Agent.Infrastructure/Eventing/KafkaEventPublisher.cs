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
        ApplySecurity(config, options);
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

    internal static void ApplySecurity(ClientConfig config, KafkaOptions options)
    {
        if (!Enum.TryParse<SecurityProtocol>(options.SecurityProtocol, ignoreCase: true, out var protocol))
            protocol = SecurityProtocol.Plaintext;
        config.SecurityProtocol = protocol;
        if (!string.IsNullOrWhiteSpace(options.SslCaLocation)) config.SslCaLocation = options.SslCaLocation;
        if (!string.IsNullOrWhiteSpace(options.SslCertificateLocation)) config.SslCertificateLocation = options.SslCertificateLocation;
        if (!string.IsNullOrWhiteSpace(options.SslKeyLocation)) config.SslKeyLocation = options.SslKeyLocation;
        if (!string.IsNullOrWhiteSpace(options.SslKeyPassword)) config.SslKeyPassword = options.SslKeyPassword;
        if (!string.IsNullOrWhiteSpace(options.SaslMechanism) && Enum.TryParse<SaslMechanism>(options.SaslMechanism, true, out var mechanism))
            config.SaslMechanism = mechanism;
        if (!string.IsNullOrWhiteSpace(options.SaslUsername)) config.SaslUsername = options.SaslUsername;
        if (!string.IsNullOrWhiteSpace(options.SaslPassword)) config.SaslPassword = options.SaslPassword;
    }
}

public sealed class KafkaOptions
{
    public string BootstrapServers { get; set; } = "localhost:9092";
    public string SecurityProtocol { get; set; } = "Plaintext";
    public string? SslCaLocation { get; set; }
    public string? SslCertificateLocation { get; set; }
    public string? SslKeyLocation { get; set; }
    public string? SslKeyPassword { get; set; }
    public string? SaslMechanism { get; set; }
    public string? SaslUsername { get; set; }
    public string? SaslPassword { get; set; }
}
