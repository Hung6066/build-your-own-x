namespace Hope.Agent.Application.Billing;

/// <summary>
/// Per-tenant cost attribution and budget enforcement. Closes gap C-3.
/// Called before/after every LLM call in the orchestrator to ensure tenants
/// don't exceed their monthly budget and to feed usage data into the billing
/// analytics pipeline (Kafka → TimescaleDB → Grafana).
/// </summary>
public interface ITenantBillingService
{
    /// <summary>Check whether the tenant has remaining budget before a model call.</summary>
    /// <returns>true if the call is allowed; false if the tenant is over budget.</returns>
    Task<bool> CheckBudgetAsync(Guid tenantId, string model, int estimatedTokens, CancellationToken ct);

    /// <summary>Record actual token usage and cost after a successful LLM call.</summary>
    Task RecordUsageAsync(UsageRecord record, CancellationToken ct);

    /// <summary>Retrieve the current budget state for a tenant (used by dashboards).</summary>
    Task<TenantBudget> GetBudgetAsync(Guid tenantId, CancellationToken ct);

    /// <summary>Set or update the monthly budget cap for a tenant.</summary>
    Task SetBudgetCapAsync(Guid tenantId, decimal monthlyCapUsd, CancellationToken ct);
}

/// <summary>Immutable record capturing one LLM usage event for billing.</summary>
public sealed record UsageRecord(
    Guid TenantId,
    Guid UserId,
    Guid? ConversationId,
    string Provider,
    string Model,
    string Intent,
    int PromptTokens,
    int CompletionTokens,
    decimal CostUsd,
    DateTimeOffset Timestamp);

/// <summary>Current budget state snapshot for a single tenant.</summary>
public sealed record TenantBudget(
    Guid TenantId,
    decimal MonthlyCapUsd,
    decimal ConsumedThisMonthUsd,
    decimal RemainingUsd,
    int TotalRequestsThisMonth,
    DateTimeOffset ResetAt);
