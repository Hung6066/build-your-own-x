using Hope.Agent.Domain.Memory;

namespace Hope.Agent.Application.Memory;

/// <summary>
/// Context for a single consolidation pass over one finished conversation turn.
/// </summary>
public sealed record MemoryConsolidationContext(
    Guid UserId,
    Guid? ConversationId,
    string UserMessage,
    string AssistantReply,
    string? AgentProfile);

/// <summary>
/// Mem0/A-Mem-style intelligent memory writer. Instead of dumping raw "user asked / assistant said"
/// transcripts, it asks an LLM to extract durable atomic facts, then reconciles each against existing
/// memories — deciding ADD (new fact), UPDATE (supersede an outdated fact), DELETE (retract a
/// contradicted fact) or NOOP (already known). This keeps long-term memory dense, current and free of
/// contradictions. Implementations must be fail-open: a consolidation error must never break the turn.
/// </summary>
public interface IMemoryConsolidator
{
    Task ConsolidateAsync(MemoryConsolidationContext context, CancellationToken ct);
}

public enum MemoryOperationKind
{
    Noop = 0,
    Add = 1,
    Update = 2,
    Delete = 3,
}

/// <summary>One reconciliation decision produced by the consolidation LLM.</summary>
public sealed record MemoryOperation(
    MemoryOperationKind Op,
    string Content,
    MemoryKind Kind,
    float Importance,
    Guid? TargetId);
