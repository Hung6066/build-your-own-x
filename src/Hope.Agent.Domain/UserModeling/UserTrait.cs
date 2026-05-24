namespace Hope.Agent.Domain.UserModeling;

/// <summary>
/// Lightweight projection of a clinician's profile (role, specialty, comm style, language)
/// extracted from conversation turns. Authoritative copy lives in Neo4j as <c>:Clinician</c>;
/// this row is the Postgres cache used to inject into the system prompt without hitting Neo4j every turn.
/// </summary>
public sealed class UserTrait
{
    public Guid UserId { get; init; }
    public string? Role { get; set; }
    public string? Specialty { get; set; }
    public string? CommunicationStyle { get; set; }
    public string? PreferredLanguage { get; set; }
    public int TurnsAtLastExtract { get; set; }
    public DateTimeOffset UpdatedAt { get; set; }
}
