using Hope.Agent.Application.Knowledge;
using Hope.Agent.Domain.Knowledge;
using Microsoft.Extensions.Logging;
using Neo4j.Driver;

namespace Hope.Agent.Infrastructure.Knowledge;

public sealed class Neo4jOptions
{
    public string Uri { get; set; } = "bolt://localhost:7687";
    public string Username { get; set; } = "neo4j";
    public string Password { get; set; } = "neo4j";
    public string Database { get; set; } = "neo4j";
}

internal sealed class Neo4jKnowledgeGraphStore(IDriver driver, Neo4jOptions options, ILogger<Neo4jKnowledgeGraphStore> log) : IKnowledgeGraphStore, IAsyncDisposable
{
    private Action<SessionConfigBuilder> Session() => b => b.WithDatabase(options.Database);

    public async Task UpsertAsync(ExtractedKnowledge extracted, CancellationToken ct)
    {
        if (extracted.Entities.Count == 0 && extracted.Relations.Count == 0) return;

        await using var session = driver.AsyncSession(Session());
        try
        {
            await session.ExecuteWriteAsync(async tx =>
            {
                foreach (var e in extracted.Entities)
                {
                    await tx.RunAsync(@"
                        MERGE (n:Entity {id: $id})
                        ON CREATE SET n.name=$name, n.type=$type, n.description=$desc,
                                      n.firstSeen=$firstSeen, n.mentions=1
                        ON MATCH  SET n.lastSeen=$lastSeen, n.mentions=coalesce(n.mentions,0)+1,
                                      n.description=coalesce(n.description, $desc)",
                        new
                        {
                            id = e.Id,
                            name = e.Name,
                            type = e.Type,
                            desc = (object?)e.Description ?? "",
                            firstSeen = e.FirstSeen.UtcDateTime,
                            lastSeen = e.LastSeen.UtcDateTime,
                        });
                }

                foreach (var r in extracted.Relations)
                {
                    await tx.RunAsync(@"
                        MATCH (a:Entity {id: $src}), (b:Entity {id: $tgt})
                        MERGE (a)-[rel:REL {predicate: $pred}]->(b)
                        ON CREATE SET rel.confidence=$conf, rel.evidence=$evidence, rel.observedAt=$at, rel.count=1
                        ON MATCH  SET rel.confidence = (rel.confidence + $conf) / 2.0,
                                      rel.count = coalesce(rel.count, 0) + 1,
                                      rel.observedAt=$at",
                        new
                        {
                            src = r.SourceId,
                            tgt = r.TargetId,
                            pred = r.Predicate,
                            conf = r.Confidence,
                            evidence = (object?)r.Evidence ?? "",
                            at = r.ObservedAt.UtcDateTime,
                        });
                }
            });
        }
        catch (Exception ex)
        {
            log.LogWarning(ex, "Neo4j upsert failed");
        }
    }

    public async Task<IReadOnlyList<KgEntity>> SearchEntitiesAsync(string query, int take, CancellationToken ct)
    {
        await using var session = driver.AsyncSession(Session());
        try
        {
            var cursor = await session.RunAsync(@"
                MATCH (n:Entity)
                WHERE toLower(n.name) CONTAINS toLower($q) OR toLower(coalesce(n.description, '')) CONTAINS toLower($q)
                RETURN n ORDER BY n.mentions DESC LIMIT $take",
                new { q = query, take });
            var list = new List<KgEntity>();
            await foreach (var rec in cursor)
            {
                var n = rec["n"].As<INode>();
                list.Add(new KgEntity
                {
                    Id = n.Properties.GetValueOrDefault("id")?.ToString() ?? "",
                    Name = n.Properties.GetValueOrDefault("name")?.ToString() ?? "",
                    Type = n.Properties.GetValueOrDefault("type")?.ToString() ?? "Concept",
                    Description = n.Properties.GetValueOrDefault("description")?.ToString(),
                    FirstSeen = ToOffset(n.Properties.GetValueOrDefault("firstSeen")),
                    LastSeen = ToOffset(n.Properties.GetValueOrDefault("lastSeen")),
                    Mentions = ToInt(n.Properties.GetValueOrDefault("mentions")),
                });
            }
            return list;
        }
        catch (Exception ex)
        {
            log.LogWarning(ex, "Neo4j search failed");
            return Array.Empty<KgEntity>();
        }
    }

    public async Task<IReadOnlyList<KgNeighbor>> NeighborsAsync(string entityId, int depth, CancellationToken ct)
    {
        await using var session = driver.AsyncSession(Session());
        try
        {
            var d = Math.Clamp(depth, 1, 3);
            var cursor = await session.RunAsync($@"
                MATCH (a:Entity {{id: $id}})-[r:REL*1..{d}]->(b:Entity)
                RETURN DISTINCT b.id AS id, b.name AS name, b.type AS type,
                       last([x IN r | x.predicate]) AS pred,
                       last([x IN r | x.confidence]) AS conf
                LIMIT 50",
                new { id = entityId });
            var list = new List<KgNeighbor>();
            await foreach (var rec in cursor)
            {
                list.Add(new KgNeighbor(
                    rec["id"].As<string>() ?? "",
                    rec["name"].As<string>() ?? "",
                    rec["type"].As<string>() ?? "",
                    rec["pred"].As<string>() ?? "REL",
                    rec["conf"].As<double?>() ?? 0));
            }
            return list;
        }
        catch (Exception ex)
        {
            log.LogWarning(ex, "Neo4j neighbors failed");
            return Array.Empty<KgNeighbor>();
        }
    }

    private static DateTimeOffset ToOffset(object? o) => o switch
    {
        DateTime dt => new DateTimeOffset(DateTime.SpecifyKind(dt, DateTimeKind.Utc)),
        ZonedDateTime zdt => zdt.ToDateTimeOffset(),
        LocalDateTime ldt => new DateTimeOffset(ldt.ToDateTime(), TimeSpan.Zero),
        _ => DateTimeOffset.MinValue,
    };

    private static int ToInt(object? o) => o switch
    {
        long l => (int)l,
        int i => i,
        _ => 0,
    };

    public async ValueTask DisposeAsync() => await driver.DisposeAsync();
}
