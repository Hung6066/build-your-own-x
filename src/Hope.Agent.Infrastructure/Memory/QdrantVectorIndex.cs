using System.Collections.Concurrent;
using Hope.Agent.Application.Rag;
using Qdrant.Client;
using Qdrant.Client.Grpc;

namespace Hope.Agent.Infrastructure.Memory;

public sealed class QdrantVectorIndex(QdrantClient client) : IVectorIndex
{
    private readonly ConcurrentDictionary<string, byte> _knownCollections = new(StringComparer.Ordinal);

    public async Task EnsureCollectionAsync(string collection, int dimension, CancellationToken ct)
    {
        if (_knownCollections.ContainsKey(collection)) return;
        if (!await client.CollectionExistsAsync(collection, ct))
        {
            await client.CreateCollectionAsync(collection, new VectorParams
            {
                Size = (ulong)dimension,
                Distance = Distance.Cosine,
            }, cancellationToken: ct);
        }
        _knownCollections[collection] = 1;
    }

    public async Task UpsertAsync(string collection, IReadOnlyList<VectorPoint> points, CancellationToken ct)
    {
        if (points.Count == 0) return;
        var qPoints = new List<PointStruct>(points.Count);
        foreach (var p in points)
        {
            var ps = new PointStruct
            {
                Id = new PointId { Uuid = p.Id.ToString() },
                Vectors = p.Embedding.ToArray(),
            };
            foreach (var kv in p.Payload)
            {
                ps.Payload[kv.Key] = kv.Value;
            }
            qPoints.Add(ps);
        }
        await client.UpsertAsync(collection, qPoints, cancellationToken: ct);
    }

    public async Task<IReadOnlyList<VectorSearchHit>> SearchAsync(
        string collection,
        ReadOnlyMemory<float> query,
        int topK,
        Dictionary<string, string>? mustEqual,
        CancellationToken ct)
    {
        Filter? filter = null;
        if (mustEqual is { Count: > 0 })
        {
            filter = new Filter();
            foreach (var kv in mustEqual)
            {
                filter.Must.Add(new Condition { Field = new FieldCondition { Key = kv.Key, Match = new Match { Keyword = kv.Value } } });
            }
        }
        var results = await client.SearchAsync(collection, query.ToArray(), filter, limit: (ulong)topK, cancellationToken: ct);
        return results.Select(r =>
        {
            var payload = new Dictionary<string, string>(StringComparer.Ordinal);
            foreach (var kv in r.Payload)
            {
                payload[kv.Key] = kv.Value.KindCase switch
                {
                    Value.KindOneofCase.StringValue => kv.Value.StringValue,
                    Value.KindOneofCase.IntegerValue => kv.Value.IntegerValue.ToString(System.Globalization.CultureInfo.InvariantCulture),
                    Value.KindOneofCase.DoubleValue => kv.Value.DoubleValue.ToString(System.Globalization.CultureInfo.InvariantCulture),
                    Value.KindOneofCase.BoolValue => kv.Value.BoolValue ? "true" : "false",
                    _ => kv.Value.ToString() ?? string.Empty,
                };
            }
            return new VectorSearchHit(Guid.Parse(r.Id.Uuid), r.Score, payload);
        }).ToList();
    }

    public async Task DeleteByDocumentAsync(string collection, Guid documentId, CancellationToken ct)
    {
        var filter = new Filter();
        filter.Must.Add(new Condition { Field = new FieldCondition { Key = "document_id", Match = new Match { Keyword = documentId.ToString() } } });
        await client.DeleteAsync(collection, filter, cancellationToken: ct);
    }
}
