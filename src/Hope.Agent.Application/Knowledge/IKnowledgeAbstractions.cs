using Hope.Agent.Domain.Knowledge;

namespace Hope.Agent.Application.Knowledge;

public interface IKnowledgeGraphStore
{
    Task UpsertAsync(ExtractedKnowledge extracted, CancellationToken ct);
    Task<IReadOnlyList<KgEntity>> SearchEntitiesAsync(string query, int take, CancellationToken ct);
    Task<IReadOnlyList<KgNeighbor>> NeighborsAsync(string entityId, int depth, CancellationToken ct);
    /// <summary>
    /// Links a long-term memory record to the knowledge-graph entities it mentions, creating a
    /// <c>(:Memory)-[:MENTIONS]-&gt;(:Entity)</c> edge for each. Enables graph-aware memory recall
    /// (retrieve memories by entity and traverse related entities). Fail-open.
    /// </summary>
    Task LinkMemoryAsync(Guid memoryId, Guid userId, IReadOnlyList<string> entityIds, CancellationToken ct);
    /// <summary>Returns memory ids that mention the given entity, most-recent first.</summary>
    Task<IReadOnlyList<Guid>> MemoriesForEntityAsync(string entityId, int take, CancellationToken ct);
}

public sealed record KgNeighbor(string EntityId, string Name, string Type, string Predicate, double Confidence);

public interface IKnowledgeExtractor
{
    Task<ExtractedKnowledge> ExtractAsync(string text, CancellationToken ct);
}
