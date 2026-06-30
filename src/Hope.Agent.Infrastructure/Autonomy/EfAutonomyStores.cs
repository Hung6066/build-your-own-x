using Hope.Agent.Application.Autonomy;
using Hope.Agent.Domain.Autonomy;
using Hope.Agent.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using Hope.Agent.Application.Governance;
using Hope.Agent.Application.Eventing;
using Hope.Agent.Application.Security;
using Hope.Agent.Infrastructure.Eventing;
using System.Text.Json;

namespace Hope.Agent.Infrastructure.Autonomy;

internal sealed class EfAgentDecisionStore(
    IDbContextFactory<AgentDbContext> dbFactory,
    IOptionsMonitor<AgentVersionOptions> versions) : IAgentDecisionStore
{
    public async Task<AgentDecision> AddAsync(AgentDecisionWrite decision, CancellationToken ct)
    {
        await using var db = await dbFactory.CreateDbContextAsync(ct).ConfigureAwait(false);
        var entity = new AgentDecision
        {
            Id = Guid.CreateVersion7(),
            TenantId = decision.TenantId ?? SecurityDefaults.DefaultTenantId,
            DecisionId = $"DEC-{DateTimeOffset.UtcNow:yyyyMMdd}-{Guid.NewGuid().ToString("N")[..10].ToUpperInvariant()}",
            UserId = decision.UserId,
            PatientId = decision.PatientId,
            ConversationId = decision.ConversationId,
            Intent = decision.Intent,
            AgentProfile = decision.AgentProfile,
            InputSummary = decision.InputSummary,
            MemoryRefsJson = NormalizeJson(decision.MemoryRefsJson),
            EvidenceJson = NormalizeJson(decision.EvidenceJson),
            ProposedActionJson = NormalizeJson(decision.ProposedActionJson),
            RiskLevel = decision.RiskLevel,
            Confidence = decision.Confidence,
            PolicyDecision = decision.PolicyDecision,
            DecisionStatus = decision.DecisionStatus,
            Reason = decision.Reason,
            DeploymentVersion = decision.DeploymentVersion ?? versions.CurrentValue.DeploymentVersion,
            PromptVersion = decision.PromptVersion ?? versions.CurrentValue.PromptVersion,
            ModelVersion = decision.ModelVersion ?? versions.CurrentValue.ModelVersion,
            ToolsetVersion = decision.ToolsetVersion ?? versions.CurrentValue.ToolsetVersion,
            PolicyVersion = decision.PolicyVersion ?? versions.CurrentValue.PolicyVersion,
            CreatedAt = DateTimeOffset.UtcNow,
            CorrelationId = decision.CorrelationId,
        };
        await db.AgentDecisions.AddAsync(entity, ct).ConfigureAwait(false);
        await db.OutboxEvents.AddAsync(EfOutboxStore.ToEntity(new OutboxEventWrite(
            entity.TenantId,
            "hope.agent.decisions",
            entity.DecisionId,
            JsonSerializer.Serialize(new
            {
                entity.DecisionId,
                entity.TenantId,
                entity.UserId,
                entity.PatientId,
                entity.Intent,
                entity.AgentProfile,
                entity.RiskLevel,
                entity.PolicyDecision,
                entity.DecisionStatus,
                entity.Confidence,
                entity.CreatedAt,
            }),
            CorrelationId: entity.CorrelationId,
            IdempotencyKey: $"decision:{entity.DecisionId}")), ct).ConfigureAwait(false);
        await db.SaveChangesAsync(ct).ConfigureAwait(false);
        return entity;
    }

    public async Task<IReadOnlyList<AgentDecision>> QueryAsync(Guid? patientId, Guid? userId, DateTimeOffset from, DateTimeOffset until, int take, CancellationToken ct)
    {
        await using var db = await dbFactory.CreateDbContextAsync(ct).ConfigureAwait(false);
        var query = db.AgentDecisions.AsNoTracking().Where(x => x.CreatedAt >= from && x.CreatedAt <= until);
        if (patientId is { } p) query = query.Where(x => x.PatientId == p);
        if (userId is { } u) query = query.Where(x => x.UserId == u);
        return await query.OrderByDescending(x => x.CreatedAt).Take(take).ToListAsync(ct).ConfigureAwait(false);
    }

    public async Task<AgentDecision?> GetByDecisionIdAsync(string decisionId, CancellationToken ct)
    {
        await using var db = await dbFactory.CreateDbContextAsync(ct).ConfigureAwait(false);
        return await db.AgentDecisions.FirstOrDefaultAsync(x => x.DecisionId == decisionId, ct).ConfigureAwait(false);
    }

    public async Task UpdateStatusAsync(string decisionId, AgentDecisionStatus status, string? reason, CancellationToken ct)
    {
        await using var db = await dbFactory.CreateDbContextAsync(ct).ConfigureAwait(false);
        await db.AgentDecisions
            .Where(x => x.DecisionId == decisionId)
            .ExecuteUpdateAsync(s => s
                .SetProperty(x => x.DecisionStatus, status)
                .SetProperty(x => x.Reason, reason), ct)
            .ConfigureAwait(false);
    }

    private static string? NormalizeJson(string? json) => string.IsNullOrWhiteSpace(json) ? null : json;
}

internal sealed class EfAutonomousActionStore(
    IDbContextFactory<AgentDbContext> dbFactory,
    IOptionsMonitor<AgentVersionOptions> versions) : IAutonomousActionStore
{
    public async Task<AutonomousAction> AddAsync(AutonomousActionWrite action, CancellationToken ct)
    {
        await using var db = await dbFactory.CreateDbContextAsync(ct).ConfigureAwait(false);
        var entity = new AutonomousAction
        {
            Id = Guid.CreateVersion7(),
            TenantId = action.TenantId ?? SecurityDefaults.DefaultTenantId,
            ActionId = $"ACT-{DateTimeOffset.UtcNow:yyyyMMdd}-{Guid.NewGuid().ToString("N")[..10].ToUpperInvariant()}",
            DecisionId = action.DecisionId,
            ToolName = action.ToolName,
            ArgumentsJson = action.ArgumentsJson,
            RiskLevel = action.RiskLevel,
            Confidence = action.Confidence,
            Status = action.Status,
            ScheduledFor = action.ScheduledFor,
            IdempotencyKey = action.IdempotencyKey,
            QueueBackend = string.IsNullOrWhiteSpace(action.QueueBackend) ? "postgres-ledger" : action.QueueBackend,
            CompensationToolName = action.CompensationToolName,
            CompensationArgumentsJson = action.CompensationArgumentsJson,
            DeploymentVersion = action.DeploymentVersion ?? versions.CurrentValue.DeploymentVersion,
            PromptVersion = action.PromptVersion ?? versions.CurrentValue.PromptVersion,
            ModelVersion = action.ModelVersion ?? versions.CurrentValue.ModelVersion,
            ToolsetVersion = action.ToolsetVersion ?? versions.CurrentValue.ToolsetVersion,
            PolicyVersion = action.PolicyVersion ?? versions.CurrentValue.PolicyVersion,
            CreatedAt = DateTimeOffset.UtcNow,
            CorrelationId = action.CorrelationId,
        };
        await db.AutonomousActions.AddAsync(entity, ct).ConfigureAwait(false);
        await db.OutboxEvents.AddAsync(EfOutboxStore.ToEntity(new OutboxEventWrite(
            entity.TenantId,
            "hope.autonomy.actions",
            entity.ActionId,
            JsonSerializer.Serialize(new
            {
                entity.ActionId,
                entity.DecisionId,
                entity.TenantId,
                entity.ToolName,
                entity.RiskLevel,
                entity.Status,
                entity.ScheduledFor,
                entity.IdempotencyKey,
                entity.CreatedAt,
            }),
            CorrelationId: entity.CorrelationId,
            IdempotencyKey: $"action:{entity.ActionId}")), ct).ConfigureAwait(false);
        await db.SaveChangesAsync(ct).ConfigureAwait(false);
        return entity;
    }

    public async Task<AutonomousAction?> GetByActionIdAsync(string actionId, CancellationToken ct)
    {
        await using var db = await dbFactory.CreateDbContextAsync(ct).ConfigureAwait(false);
        return await db.AutonomousActions.FirstOrDefaultAsync(x => x.ActionId == actionId, ct).ConfigureAwait(false);
    }

    public async Task<IReadOnlyList<AutonomousAction>> QueryAsync(AutonomousActionStatus? status, DateTimeOffset from, DateTimeOffset until, int take, CancellationToken ct)
    {
        await using var db = await dbFactory.CreateDbContextAsync(ct).ConfigureAwait(false);
        var query = db.AutonomousActions.AsNoTracking().Where(x => x.CreatedAt >= from && x.CreatedAt <= until);
        if (status is { } s) query = query.Where(x => x.Status == s);
        return await query.OrderByDescending(x => x.CreatedAt).Take(take).ToListAsync(ct).ConfigureAwait(false);
    }

    public async Task<IReadOnlyList<AutonomousAction>> DueAsync(DateTimeOffset now, int take, CancellationToken ct)
    {
        await using var db = await dbFactory.CreateDbContextAsync(ct).ConfigureAwait(false);
        return await db.AutonomousActions
            .Where(x => x.Status == AutonomousActionStatus.Approved
                && (x.ScheduledFor == null || x.ScheduledFor <= now)
                && x.AttemptCount < 3)
            .OrderBy(x => x.ScheduledFor ?? x.CreatedAt)
            .ThenBy(x => x.CreatedAt)
            .Take(take)
            .ToListAsync(ct)
            .ConfigureAwait(false);
    }

    public async Task UpdateAsync(AutonomousAction action, CancellationToken ct)
    {
        await using var db = await dbFactory.CreateDbContextAsync(ct).ConfigureAwait(false);
        db.AutonomousActions.Update(action);
        await db.SaveChangesAsync(ct).ConfigureAwait(false);
    }
}

internal sealed class EfAutonomyGoalStore(IDbContextFactory<AgentDbContext> dbFactory) : IAutonomyGoalStore
{
    public async Task<AutonomyGoal> AddAsync(AutonomyGoalWrite goal, CancellationToken ct)
    {
        await using var db = await dbFactory.CreateDbContextAsync(ct).ConfigureAwait(false);
        var entity = new AutonomyGoal
        {
            Id = Guid.CreateVersion7(),
            GoalId = $"GOAL-{DateTimeOffset.UtcNow:yyyyMMdd}-{Guid.NewGuid().ToString("N")[..10].ToUpperInvariant()}",
            PatientId = goal.PatientId,
            UserId = goal.UserId,
            GoalType = goal.GoalType,
            Description = goal.Description,
            EvidenceJson = NormalizeJson(goal.EvidenceJson) ?? "[]",
            PriorityScore = goal.PriorityScore,
            Confidence = goal.Confidence,
            MaxAllowedRisk = goal.MaxAllowedRisk,
            Status = goal.Status,
            DecisionId = goal.DecisionId,
            Reason = goal.Reason,
            CreatedAt = DateTimeOffset.UtcNow,
            CorrelationId = goal.CorrelationId,
        };
        await db.AutonomyGoals.AddAsync(entity, ct).ConfigureAwait(false);
        await db.SaveChangesAsync(ct).ConfigureAwait(false);
        return entity;
    }

    public async Task<IReadOnlyList<AutonomyGoal>> QueryAsync(Guid? patientId, AutonomyGoalStatus? status, DateTimeOffset from, DateTimeOffset until, int take, CancellationToken ct)
    {
        await using var db = await dbFactory.CreateDbContextAsync(ct).ConfigureAwait(false);
        var query = db.AutonomyGoals.AsNoTracking().Where(x => x.CreatedAt >= from && x.CreatedAt <= until);
        if (patientId is { } p) query = query.Where(x => x.PatientId == p);
        if (status is { } s) query = query.Where(x => x.Status == s);
        return await query.OrderByDescending(x => x.CreatedAt).Take(take).ToListAsync(ct).ConfigureAwait(false);
    }

    public async Task UpdateStatusAsync(string goalId, AutonomyGoalStatus status, string? decisionId, string? reason, CancellationToken ct)
    {
        await using var db = await dbFactory.CreateDbContextAsync(ct).ConfigureAwait(false);
        await db.AutonomyGoals
            .Where(x => x.GoalId == goalId)
            .ExecuteUpdateAsync(s => s
                .SetProperty(x => x.Status, status)
                .SetProperty(x => x.DecisionId, decisionId)
                .SetProperty(x => x.Reason, reason)
                .SetProperty(x => x.CompletedAt, status == AutonomyGoalStatus.Completed || status == AutonomyGoalStatus.Failed ? DateTimeOffset.UtcNow : (DateTimeOffset?)null), ct)
            .ConfigureAwait(false);
    }

    private static string? NormalizeJson(string? json) => string.IsNullOrWhiteSpace(json) ? null : json;
}

internal sealed class EfAutonomyReflectionStore(IDbContextFactory<AgentDbContext> dbFactory) : IAutonomyReflectionStore
{
    public async Task<AutonomyReflection> AddAsync(AutonomyReflectionWrite reflection, CancellationToken ct)
    {
        await using var db = await dbFactory.CreateDbContextAsync(ct).ConfigureAwait(false);
        var entity = new AutonomyReflection
        {
            Id = Guid.CreateVersion7(),
            ReflectionId = $"REF-{DateTimeOffset.UtcNow:yyyyMMdd}-{Guid.NewGuid().ToString("N")[..10].ToUpperInvariant()}",
            GoalId = reflection.GoalId,
            DecisionId = reflection.DecisionId,
            ActionId = reflection.ActionId,
            PatientId = reflection.PatientId,
            Succeeded = reflection.Succeeded,
            Summary = reflection.Summary,
            LessonsJson = string.IsNullOrWhiteSpace(reflection.LessonsJson) ? "[]" : reflection.LessonsJson,
            ConfidenceDelta = reflection.ConfidenceDelta,
            CorrelationId = reflection.CorrelationId,
            CreatedAt = DateTimeOffset.UtcNow,
        };
        await db.AutonomyReflections.AddAsync(entity, ct).ConfigureAwait(false);
        await db.SaveChangesAsync(ct).ConfigureAwait(false);
        return entity;
    }

    public async Task<IReadOnlyList<AutonomyReflection>> QueryAsync(Guid? patientId, DateTimeOffset from, DateTimeOffset until, int take, CancellationToken ct)
    {
        await using var db = await dbFactory.CreateDbContextAsync(ct).ConfigureAwait(false);
        var query = db.AutonomyReflections.AsNoTracking().Where(x => x.CreatedAt >= from && x.CreatedAt <= until);
        if (patientId is { } p) query = query.Where(x => x.PatientId == p);
        return await query.OrderByDescending(x => x.CreatedAt).Take(take).ToListAsync(ct).ConfigureAwait(false);
    }
}

internal sealed class EfAutonomyLearningFactStore(IDbContextFactory<AgentDbContext> dbFactory) : IAutonomyLearningFactStore
{
    public async Task<AutonomyLearningFact> UpsertAsync(AutonomyLearningFactWrite fact, CancellationToken ct)
    {
        await using var db = await dbFactory.CreateDbContextAsync(ct).ConfigureAwait(false);
        var entity = await db.AutonomyLearningFacts
            .FirstOrDefaultAsync(x => x.Kind == fact.Kind && x.Key == fact.Key, ct)
            .ConfigureAwait(false);
        if (entity is null)
        {
            entity = new AutonomyLearningFact
            {
                Id = Guid.CreateVersion7(),
                FactId = $"FACT-{DateTimeOffset.UtcNow:yyyyMMdd}-{Guid.NewGuid().ToString("N")[..10].ToUpperInvariant()}",
                Kind = fact.Kind,
                Key = fact.Key,
                CreatedAt = DateTimeOffset.UtcNow,
            };
            await db.AutonomyLearningFacts.AddAsync(entity, ct).ConfigureAwait(false);
        }

        entity.ValueJson = string.IsNullOrWhiteSpace(fact.ValueJson) ? "{}" : fact.ValueJson;
        entity.Confidence = Math.Clamp(fact.Confidence, 0, 1);
        entity.Source = fact.Source;
        entity.LastObservedAt = DateTimeOffset.UtcNow;
        await db.SaveChangesAsync(ct).ConfigureAwait(false);
        return entity;
    }

    public async Task<IReadOnlyList<AutonomyLearningFact>> QueryAsync(AutonomyLearningFactKind? kind, int take, CancellationToken ct)
    {
        await using var db = await dbFactory.CreateDbContextAsync(ct).ConfigureAwait(false);
        var query = db.AutonomyLearningFacts.AsNoTracking();
        if (kind is { } k) query = query.Where(x => x.Kind == k);
        return await query.OrderByDescending(x => x.LastObservedAt ?? x.CreatedAt).Take(take).ToListAsync(ct).ConfigureAwait(false);
    }
}
