namespace Hope.Agent.Domain.Knowledge;

public sealed class KgEntity
{
    public required string Id { get; init; }              // canonical id (slug of name+type)
    public required string Name { get; init; }
    public required string Type { get; init; }            // Person, Drug, Condition, Procedure, Facility, Concept
    public string? Description { get; init; }
    public DateTimeOffset FirstSeen { get; init; }
    public DateTimeOffset LastSeen { get; set; }
    public int Mentions { get; set; }
}

public sealed class KgRelation
{
    public required string SourceId { get; init; }
    public required string TargetId { get; init; }
    public required string Predicate { get; init; }       // TREATS, INDICATED_FOR, CONTRAINDICATES, WORKS_AT, ...
    public double Confidence { get; init; }
    public string? Evidence { get; init; }
    public DateTimeOffset ObservedAt { get; init; }
}

public sealed class ExtractedKnowledge
{
    public IReadOnlyList<KgEntity> Entities { get; init; } = Array.Empty<KgEntity>();
    public IReadOnlyList<KgRelation> Relations { get; init; } = Array.Empty<KgRelation>();
}
