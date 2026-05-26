using Hope.Agent.Application.Abstractions;
using Hope.Agent.Domain.Memory;
using Qdrant.Client;
using Qdrant.Client.Grpc;

namespace Hope.Agent.Infrastructure.Memory;

public sealed class QdrantMemoryStore(QdrantClient client, QdrantOptions options) : IMemoryStore
{
    public async Task UpsertAsync(MemoryRecord record, ReadOnlyMemory<float> embedding, CancellationToken ct)
    {
        await EnsureCollectionAsync(embedding.Length, ct);
        var point = new PointStruct
        {
            Id = new PointId { Uuid = record.Id.ToString() },
            Vectors = embedding.ToArray(),
        };
        point.Payload["user_id"] = record.UserId.ToString();
        point.Payload["conversation_id"] = record.ConversationId?.ToString() ?? string.Empty;
        point.Payload["kind"] = (int)record.Kind;
        point.Payload["content"] = record.Content;
        point.Payload["source"] = record.Source ?? string.Empty;
        point.Payload["importance"] = (double)record.Importance;
        point.Payload["created_at"] = record.CreatedAt.ToUnixTimeMilliseconds();
        if (record.Metadata.Count > 0)
            point.Payload["metadata"] = System.Text.Json.JsonSerializer.Serialize(record.Metadata);
        await client.UpsertAsync(options.Collection, [point], cancellationToken: ct);
    }

    /// <summary>
    /// Retrieves the top-K memories for a user, re-ranked by an effective score that combines
    /// raw cosine similarity, stored importance, and recency decay (90-day half-life).
    /// Fetches <c>topK * 3</c> raw candidates from Qdrant to allow meaningful re-ordering.
    /// </summary>
    public async Task<IReadOnlyList<MemorySearchHit>> SearchAsync(Guid userId, ReadOnlyMemory<float> query, int topK, MemoryKind? kind, CancellationToken ct)
    {
        var filter = BuildUserFilter(userId, kind);
        var candidateLimit = (ulong)Math.Max(topK * 3, 15);
        var results = await client.SearchAsync(options.Collection, query.ToArray(), filter, limit: candidateLimit, cancellationToken: ct);
        var now = DateTimeOffset.UtcNow;
        return results
            .Select(r => (Hit: MapToHit(r, userId), Raw: r))
            .Select(x =>
            {
                var daysSince = Math.Max(0.0, (now - x.Hit.Record.CreatedAt).TotalDays);
                var decay = (float)Math.Exp(-daysSince / 90.0);
                // Weight importance [0,1] → [0.4, 1.0] so low-importance memories still surface
                var importanceWeight = 0.4f + 0.6f * x.Hit.Record.Importance;
                return new MemorySearchHit(x.Hit.Record, x.Raw.Score * importanceWeight * decay);
            })
            .OrderByDescending(h => h.Score)
            .Take(topK)
            .ToList();
    }

    /// <summary>
    /// Returns up to 1 memory whose raw cosine similarity exceeds <paramref name="threshold"/>.
    /// Uses Qdrant-side score threshold to avoid transferring irrelevant candidates.
    /// </summary>
    public async Task<IReadOnlyList<MemorySearchHit>> FindSimilarAsync(Guid userId, ReadOnlyMemory<float> query, float threshold, CancellationToken ct)
    {
        var filter = BuildUserFilter(userId, kind: null);
        var results = await client.SearchAsync(
            options.Collection, query.ToArray(), filter,
            limit: 1, scoreThreshold: threshold, cancellationToken: ct);
        return results.Select(r => MapToHit(r, userId)).ToList();
    }

    /// <summary>
    /// Increases the stored importance of an existing memory by <paramref name="delta"/>, capped at 1.0.
    /// </summary>
    public async Task BumpImportanceAsync(Guid memoryId, float delta, CancellationToken ct)
    {
        var retrieved = await client.RetrieveAsync(options.Collection, memoryId, withPayload: true, withVectors: false, cancellationToken: ct);
        if (retrieved.Count == 0) return;
        var current = (float)retrieved[0].Payload["importance"].DoubleValue;
        var updated = Math.Min(1.0f, current + delta);
        await client.SetPayloadAsync(
            options.Collection,
            new Dictionary<string, Value> { ["importance"] = (double)updated },
            memoryId,
            cancellationToken: ct);
    }

    private static Filter BuildUserFilter(Guid userId, MemoryKind? kind)
    {
        var filter = new Filter();
        filter.Must.Add(new Condition
        {
            Field = new FieldCondition { Key = "user_id", Match = new Match { Keyword = userId.ToString() } }
        });
        if (kind is { } k)
            filter.Must.Add(new Condition
            {
                Field = new FieldCondition { Key = "kind", Match = new Match { Integer = (int)k } }
            });
        return filter;
    }

    private static MemorySearchHit MapToHit(ScoredPoint r, Guid userId) =>
        new(new MemoryRecord
        {
            Id = Guid.Parse(r.Id.Uuid),
            UserId = userId,
            Kind = (MemoryKind)(int)r.Payload["kind"].IntegerValue,
            Content = r.Payload["content"].StringValue,
            Source = r.Payload.TryGetValue("source", out var s) ? s.StringValue : null,
            Importance = (float)r.Payload["importance"].DoubleValue,
            CreatedAt = DateTimeOffset.FromUnixTimeMilliseconds(r.Payload["created_at"].IntegerValue),
            Metadata = r.Payload.TryGetValue("metadata", out var m) && !string.IsNullOrEmpty(m.StringValue)
                ? System.Text.Json.JsonSerializer.Deserialize<Dictionary<string, string>>(m.StringValue) ?? []
                : [],
        }, r.Score);

    private async Task EnsureCollectionAsync(int dim, CancellationToken ct)
    {
        var exists = await client.CollectionExistsAsync(options.Collection, ct);
        if (exists) return;
        await client.CreateCollectionAsync(options.Collection, new VectorParams
        {
            Size = (ulong)dim,
            Distance = Distance.Cosine,
        }, cancellationToken: ct);
    }
}

public sealed class QdrantOptions
{
    public string Host { get; set; } = "localhost";
    public int Port { get; set; } = 6334;
    public string Collection { get; set; } = "agent_memory";
    public string? ApiKey { get; set; }
}
