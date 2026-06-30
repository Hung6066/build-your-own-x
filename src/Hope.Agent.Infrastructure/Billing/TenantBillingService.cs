using Hope.Agent.Application.Billing;
using Hope.Agent.Application.Security;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using StackExchange.Redis;

namespace Hope.Agent.Infrastructure.Billing;

/// <summary>
/// Redis-backed tenant billing service with budget enforcement and usage tracking.
/// Closes gap C-3. Budget caps are stored in Redis for sub-millisecond reads;
/// usage events are emitted to Kafka for analytics (TimescaleDB + Grafana).
/// </summary>
internal sealed class TenantBillingService : ITenantBillingService
{
    private const string BudgetKeyPrefix = "billing:budget";
    private const string UsageCounterPrefix = "billing:usage";
    private readonly IDatabase _redis;
    private readonly ILogger<TenantBillingService> _log;
    private readonly IOptionsMonitor<DataPerimeterOptions> _perimeter;

    public TenantBillingService(IConnectionMultiplexer multiplexer, ILogger<TenantBillingService> log, IOptionsMonitor<DataPerimeterOptions> perimeter)
    {
        _redis = multiplexer.GetDatabase();
        _log = log;
        _perimeter = perimeter;
    }

    public async Task<bool> CheckBudgetAsync(Guid tenantId, string model, int estimatedTokens, CancellationToken ct)
    {
        var budgetKey = Key($"{BudgetKeyPrefix}:{tenantId:N}");
        var values = await _redis.HashGetAllAsync(budgetKey);

        if (values.Length == 0)
        {
            // No budget configured → allow by default (backwards compatible)
            return true;
        }

        var cap = ParseDecimal(values, "monthly_cap_usd");
        if (cap is null) return true; // No cap set

        var consumed = ParseDecimal(values, "consumed_usd") ?? 0m;
        var remaining = cap.Value - consumed;

        if (remaining <= 0)
        {
            _log.LogWarning("Tenant {TenantId} over budget: cap={Cap} consumed={Consumed}", tenantId, cap, consumed);
            return false;
        }

        return true;
    }

    public async Task RecordUsageAsync(UsageRecord record, CancellationToken ct)
    {
        var usageKey = Key($"{UsageCounterPrefix}:{record.TenantId:N}:{DateTimeOffset.UtcNow:yyyy-MM}");
        await _redis.HashIncrementAsync(usageKey, "consumed_usd", (long)(record.CostUsd * 1_000_000m));
        await _redis.HashIncrementAsync(usageKey, "request_count");

        // Emit to Kafka for analytics (TimescaleDB)
        // This is fire-and-forget; the Kafka sink handles buffering
        _log.LogInformation(
            "Billing usage recorded: tenant={TenantId} user={UserId} model={Model} tokens={Tokens} cost={CostUsd}",
            record.TenantId, record.UserId, record.Model,
            record.PromptTokens + record.CompletionTokens, record.CostUsd);

        // Update Prometheus metric with tenant label
        Hope.Agent.Application.Observability.HopeMeters.LlmCostUsd.Add(
            (long)(record.CostUsd * 1_000_000m),
            new("tenant", record.TenantId.ToString("N")),
            new("provider", record.Provider),
            new("model", record.Model));
    }

    public async Task<TenantBudget> GetBudgetAsync(Guid tenantId, CancellationToken ct)
    {
        var budgetKey = Key($"{BudgetKeyPrefix}:{tenantId:N}");
        var usageKey = Key($"{UsageCounterPrefix}:{tenantId:N}:{DateTimeOffset.UtcNow:yyyy-MM}");

        var budgetValues = await _redis.HashGetAllAsync(budgetKey);
        var usageValues = await _redis.HashGetAllAsync(usageKey);

        var cap = ParseDecimal(budgetValues, "monthly_cap_usd") ?? decimal.MaxValue;
        var consumed = ParseDecimal(usageValues, "consumed_usd") ?? 0m;
        var requestCount = (int)(ParseDecimal(usageValues, "request_count") ?? 0m);

        // Calculate reset time (first of next month UTC)
        var now = DateTimeOffset.UtcNow;
        var resetAt = new DateTimeOffset(now.Year, now.Month, 1, 0, 0, 0, TimeSpan.Zero).AddMonths(1);

        return new TenantBudget(tenantId, cap, consumed, cap - consumed, requestCount, resetAt);
    }

    public async Task SetBudgetCapAsync(Guid tenantId, decimal monthlyCapUsd, CancellationToken ct)
    {
        var budgetKey = Key($"{BudgetKeyPrefix}:{tenantId:N}");
        await _redis.HashSetAsync(budgetKey, "monthly_cap_usd", monthlyCapUsd.ToString("F6"));
        await _redis.KeyExpireAsync(budgetKey, TimeSpan.FromDays(90)); // auto-cleanup stale tenants
        _log.LogInformation("Budget cap set for tenant {TenantId}: ${Cap}/month", tenantId, monthlyCapUsd);
    }

    private static decimal? ParseDecimal(HashEntry[] entries, string field)
    {
        var entry = entries.FirstOrDefault(e => e.Name == field);
        if (entry.Value.IsNull) return null;
        return decimal.TryParse(entry.Value.ToString(), out var val) ? val : null;
    }

    private string Key(string suffix)
    {
        var prefix = string.IsNullOrWhiteSpace(_perimeter.CurrentValue.RedisKeyPrefix) ? "hope" : _perimeter.CurrentValue.RedisKeyPrefix.Trim(':');
        return $"{prefix}:{suffix}";
    }
}
