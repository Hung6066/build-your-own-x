namespace Hope.Agent.Domain.Security;

/// <summary>Auto-learned attack signature observed by the prompt shield.</summary>
public sealed class AdversarialPattern
{
    public Guid Id { get; init; }
    public required string Signature { get; init; }       // normalized ngram fingerprint (sha256 hex prefix)
    public required string Sample { get; init; }          // truncated raw sample
    public required string Reason { get; init; }          // initial shield reason that flagged it
    public int Hits { get; set; }
    public bool Active { get; set; }                       // promoted to live block list
    public double Confidence { get; set; }                 // hits-derived score
    public DateTimeOffset FirstSeen { get; init; }
    public DateTimeOffset LastSeen { get; set; }
    public DateTimeOffset? PromotedAt { get; set; }
}
