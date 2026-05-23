using Hope.Agent.Domain.Knowledge;

namespace Hope.Agent.Application.Knowledge;

public interface IKnowledgeGraphStore
{
    Task UpsertAsync(ExtractedKnowledge extracted, CancellationToken ct);
    Task<IReadOnlyList<KgEntity>> SearchEntitiesAsync(string query, int take, CancellationToken ct);
    Task<IReadOnlyList<KgNeighbor>> NeighborsAsync(string entityId, int depth, CancellationToken ct);
}

public sealed record KgNeighbor(string EntityId, string Name, string Type, string Predicate, double Confidence);

public interface IKnowledgeExtractor
{
    Task<ExtractedKnowledge> ExtractAsync(string text, CancellationToken ct);
}
