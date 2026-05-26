using System.Text.Json;
using Hope.Agent.Application.Abstractions;
using Hope.Agent.Application.Governance;
using Hope.Agent.Domain.Audit;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace Hope.Agent.MultiAgent.Governance;

/// <summary>
/// Decorator around <see cref="AgtGovernanceGate"/> — Phase 3 of the AGT governance integration.
///
/// Writes every policy <b>denial</b> to the <see cref="IAuditSink"/> (PostgreSQL
/// <c>audit_events</c> table) so that access-control decisions are part of the
/// immutable compliance audit trail (HIPAA § 164.312(b): audit controls).
///
/// Allowed decisions are intentionally <b>not</b> recorded here — they are
/// already implicitly captured by the conversation-level audit that
/// <c>AgentOrchestrator</c> writes.  Recording only denials keeps audit volume
/// low while preserving all evidence required for a compliance audit.
///
/// Audit writes are fully decoupled from governance decisions: a write failure
/// is logged at Warning level and the original decision is returned unchanged.
/// </summary>
internal sealed class AuditingGovernanceGate(
    AgtGovernanceGate inner,
    IServiceScopeFactory scopeFactory,
    ILogger<AuditingGovernanceGate> log) : IGovernanceGate
{
    public async ValueTask<GovernanceDecision> EvaluateIntentAsync(
        string agentDid,
        string intent,
        IReadOnlyDictionary<string, object?>? context = null,
        CancellationToken ct = default)
    {
        var decision = await inner.EvaluateIntentAsync(agentDid, intent, context, ct);

        if (!decision.Allowed)
            await WriteAuditAsync(agentDid, intent, decision, ct);

        return decision;
    }

    /// <inheritdoc/>
    public IReadOnlyList<string> ScanForForbiddenPatterns(string input)
        => inner.ScanForForbiddenPatterns(input);

    private async Task WriteAuditAsync(
        string agentDid,
        string intent,
        GovernanceDecision decision,
        CancellationToken ct)
    {
        try
        {
            await using var scope = scopeFactory.CreateAsyncScope();
            var sink = scope.ServiceProvider.GetRequiredService<IAuditSink>();
            await sink.WriteAsync(new AuditEvent
            {
                Id = Guid.CreateVersion7(),
                OccurredAt = DateTimeOffset.UtcNow,
                Actor = agentDid,
                Action = "governance.intent.denied",
                ResourceType = "intent",
                ResourceId = intent,
                Reason = decision.DenyReason
                    ?? $"Policy '{decision.PolicyName}' rule '{decision.MatchedRule}' denied",
                PayloadJson = JsonSerializer.Serialize(new
                {
                    intent,
                    decision.PolicyName,
                    decision.MatchedRule,
                    decision.DenyReason,
                }),
            }, ct);
        }
        catch (Exception ex)
        {
            // Audit failures must never affect the governance decision returned to callers.
            log.LogWarning(ex,
                "Failed to write governance audit event: intent='{Intent}' agentDid='{AgentDid}'",
                intent, agentDid);
        }
    }
}
