namespace Hope.Agent.Domain.Learning;

/// <summary>
/// A single test case belonging to an evaluation suite.
/// Cases stored in the DB take precedence over the fallback golden-suite.json file.
/// </summary>
public sealed class EvalCase
{
    public Guid Id { get; init; }

    /// <summary>Suite name, e.g. "default", "cardiology", "pediatrics".</summary>
    public required string Suite { get; init; }

    /// <summary>Human-readable name used in reports.</summary>
    public required string Name { get; init; }

    /// <summary>The user message sent to the agent during evaluation.</summary>
    public required string UserMessage { get; init; }

    /// <summary>Expected / gold-standard answer used by the judge.</summary>
    public required string ReferenceAnswer { get; init; }

    /// <summary>Optional comma-separated tags, e.g. "safety,oncology".</summary>
    public string? Tags { get; init; }

    public bool Active { get; set; }

    public DateTimeOffset CreatedAt { get; init; }
}
