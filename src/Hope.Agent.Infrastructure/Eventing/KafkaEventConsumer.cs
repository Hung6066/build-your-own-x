using System.Runtime.CompilerServices;
using System.Text;
using Confluent.Kafka;
using Hope.Agent.Application.Eventing;

namespace Hope.Agent.Infrastructure.Eventing;

public sealed class KafkaEventConsumer(KafkaOptions options) : IEventConsumer
{
    public async IAsyncEnumerable<EventEnvelope> ConsumeAsync(
        IReadOnlyCollection<string> topics,
        string groupId,
        [EnumeratorCancellation] CancellationToken ct)
    {
        var config = new ConsumerConfig
        {
            BootstrapServers = options.BootstrapServers,
            GroupId = groupId,
            EnableAutoCommit = false,
            AutoOffsetReset = AutoOffsetReset.Earliest,
            EnablePartitionEof = false,
            SessionTimeoutMs = 10_000,
            // Suppress librdkafka native stderr logs — connection retries are
            // handled by the worker loop and logged via ILogger instead.
            Debug = string.Empty,
        };
        using var consumer = new ConsumerBuilder<string, string>(config)
            .SetLogHandler((_, _) => { }) // swallow native log output
            .Build();
        consumer.Subscribe(topics);
        try
        {
            while (!ct.IsCancellationRequested)
            {
                ConsumeResult<string, string>? result = null;
                try
                {
                    result = await Task.Run(() => consumer.Consume(TimeSpan.FromSeconds(1)), ct);
                }
                catch (OperationCanceledException) { break; }
                catch (ConsumeException) { continue; }
                if (result is null || result.Message is null) continue;
                var headers = new Dictionary<string, string>(StringComparer.Ordinal);
                if (result.Message.Headers is not null)
                {
                    foreach (var h in result.Message.Headers)
                    {
                        headers[h.Key] = Encoding.UTF8.GetString(h.GetValueBytes());
                    }
                }
                yield return new EventEnvelope(result.Topic, result.Message.Key ?? string.Empty, result.Message.Value ?? string.Empty, headers);
                try { consumer.Commit(result); } catch (KafkaException) { /* ignore commit retry */ }
            }
        }
        finally
        {
            consumer.Close();
        }
    }
}
