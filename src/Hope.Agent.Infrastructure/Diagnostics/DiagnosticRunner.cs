using System.Diagnostics;
using Confluent.Kafka;
using Hope.Agent.Application.Diagnostics;
using Hope.Agent.Application.LLM;
using Hope.Agent.Infrastructure.Eventing;
using Hope.Agent.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using Neo4j.Driver;
using StackExchange.Redis;

namespace Hope.Agent.Infrastructure.Diagnostics;

internal sealed class DiagnosticRunner(
    AgentDbContext db,
    IConnectionMultiplexer redis,
    IDriver neo4j,
    KafkaOptions kafkaOpts,
    ILLMRouter llm,
    ILogger<DiagnosticRunner> log) : IDiagnosticRunner
{
    public async Task<DiagnosticReport> RunAsync(CancellationToken ct)
    {
        var checks = new List<HealthCheckResult>
        {
            await Time("postgres", async c =>
            {
                await db.Database.ExecuteSqlRawAsync("SELECT 1", c);
                return "connected";
            }, ct),
            await Time("redis", async _ =>
            {
                var pong = await redis.GetDatabase().PingAsync();
                return $"ping {pong.TotalMilliseconds:F1}ms";
            }, ct),
            await Time("neo4j", async c =>
            {
                await using var session = neo4j.AsyncSession();
                var result = await session.RunAsync("RETURN 1 AS ok");
                _ = await result.SingleAsync(r => r["ok"].As<int>());
                return "connected";
            }, ct),
            await Time("kafka", _ => Task.FromResult(ProbeKafka(kafkaOpts)), ct),
            await Time("llm", _ =>
            {
                var p = llm.SelectChat();
                var e = llm.SelectEmbedding();
                return Task.FromResult($"chat={p.Name}, embed={e.Name}");
            }, ct),
        };

        var report = new DiagnosticReport(DateTimeOffset.UtcNow, checks.TrueForAll(c => c.Healthy), checks);
        log.LogInformation("Diagnostics completed: {Healthy}/{Total} healthy.",
            checks.Count(c => c.Healthy), checks.Count);
        return report;
    }

    private static async Task<HealthCheckResult> Time(string name, Func<CancellationToken, Task<string>> probe, CancellationToken ct)
    {
        var sw = Stopwatch.StartNew();
        try
        {
            var msg = await probe(ct);
            return new HealthCheckResult(name, true, msg, sw.Elapsed);
        }
        catch (Exception ex)
        {
            return new HealthCheckResult(name, false, ex.GetType().Name + ": " + ex.Message, sw.Elapsed);
        }
    }

    private static string ProbeKafka(KafkaOptions opts)
    {
        if (string.IsNullOrWhiteSpace(opts.BootstrapServers))
            return "no bootstrap servers configured";
        var cfg = new AdminClientConfig { BootstrapServers = opts.BootstrapServers };
        using var admin = new AdminClientBuilder(cfg).Build();
        var meta = admin.GetMetadata(TimeSpan.FromSeconds(3));
        return $"brokers={meta.Brokers.Count}, topics={meta.Topics.Count}";
    }
}
