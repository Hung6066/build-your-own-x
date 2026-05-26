namespace Hope.Agent.Domain.Training;

/// <summary>
/// A doctor-rated A/B preference pair used to build DPO training data.
/// The chosen response is the one the clinician preferred; rejected is the alternative.
/// </summary>
public sealed class PreferenceRecord
{
    public Guid Id { get; init; }

    /// <summary>Source conversation that generated the responses being compared.</summary>
    public Guid ConversationId { get; init; }

    /// <summary>The turn (message id) that prompted both responses.</summary>
    public Guid MessageId { get; init; }

    /// <summary>Original user / doctor prompt for this turn (PHI-redacted before storage).</summary>
    public required string Prompt { get; init; }

    /// <summary>The response the clinician preferred (label = "chosen").</summary>
    public required string ChosenResponse { get; init; }

    /// <summary>The alternative response the clinician rejected (label = "rejected").</summary>
    public required string RejectedResponse { get; init; }

    /// <summary>LLM provider that generated the chosen response.</summary>
    public string? ChosenProvider { get; init; }

    /// <summary>LLM provider that generated the rejected response.</summary>
    public string? RejectedProvider { get; init; }

    /// <summary>Optional rationale / critique the clinician provided.</summary>
    public string? Rationale { get; init; }

    /// <summary>Clinical specialty context (e.g., "cardiology", "obstetrics").</summary>
    public string? Specialty { get; init; }

    /// <summary>Clinician who rated this pair.</summary>
    public Guid RatedByUserId { get; init; }

    public DateTimeOffset CreatedAt { get; init; }
}
