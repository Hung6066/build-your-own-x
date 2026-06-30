namespace Hope.Agent.Application.Compliance;

/// <summary>
/// GDPR "Right to Erasure" (Art. 17) implementation for Hope.Agent.
/// Closes gap C-2. Uses a phased approach: soft-delete + anonymize → cooling-off
/// (30 days) → hard-delete + crypto-shred audit keys. Audit chain integrity is
/// preserved — only the encrypted payload is shredded while hash + previous_hash
/// remain for chain verification.
/// </summary>
public interface IGdprErasureService
{
    /// <summary>
    /// Phase 1: Initiate erasure. Soft-deletes user data, anonymizes PII
    /// columns, deletes memories/vectors/knowledge-graph nodes, and records
    /// the erasure request in the audit trail (retained for legal reasons).
    /// </summary>
    Task<ErasureResult> RequestErasureAsync(Guid userId, string requestId, CancellationToken ct);

    /// <summary>
    /// Phase 2: After the 30-day cooling-off period, hard-delete all remaining
    /// user data and crypto-shred audit encryption keys so the payload becomes
    /// permanently inaccessible while keeping the hash chain intact.
    /// </summary>
    Task<ErasureResult> FinalizeErasureAsync(string requestId, CancellationToken ct);

    /// <summary>
    /// Phase 3: Verify that no traces of the user remain across all data stores.
    /// Returns a detailed report of any dangling references found.
    /// </summary>
    Task<VerificationResult> VerifyErasureCompleteAsync(Guid userId, CancellationToken ct);

    /// <summary>Get the status of an ongoing erasure request.</summary>
    Task<ErasureStatus> GetErasureStatusAsync(string requestId, CancellationToken ct);
}

public sealed record ErasureResult(
    string RequestId,
    Guid UserId,
    ErasurePhase Phase,
    bool Success,
    IReadOnlyList<string> ActionsPerformed,
    IReadOnlyList<string>? Errors = null);

public enum ErasurePhase
{
    Requested,
    SoftDeleted,
    CoolingOff,
    Finalized,
    Verified
}

public sealed record ErasureStatus(
    string RequestId,
    Guid UserId,
    ErasurePhase Phase,
    DateTimeOffset RequestedAt,
    DateTimeOffset? CoolingOffUntil,
    DateTimeOffset? FinalizedAt,
    bool IsComplete);

public sealed record VerificationResult(
    Guid UserId,
    bool IsClean,
    IReadOnlyDictionary<string, int> RemainingTraces,
    IReadOnlyList<string> Warnings);
