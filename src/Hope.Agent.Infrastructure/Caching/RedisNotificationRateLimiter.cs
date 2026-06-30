using System.Text.Json;
using Hope.Agent.Application.Tools;
using StackExchange.Redis;

namespace Hope.Agent.Infrastructure.Caching;

public sealed class RedisNotificationRateLimiter(IConnectionMultiplexer redis) : INotificationRateLimiter
{
    private sealed record BucketState(double Tokens, DateTimeOffset UpdatedAt);

    public async Task<NotificationRateLimitDecision> DecideAsync(NotificationRateLimitRequest request, CancellationToken ct)
    {
        if (string.Equals(request.Urgency, "critical", StringComparison.OrdinalIgnoreCase))
            return new NotificationRateLimitDecision("send", null, request.Capacity);

        var db = redis.GetDatabase();
        var key = $"notify_bucket:{request.PatientId}:{request.Channel}";
        var now = DateTimeOffset.UtcNow;
        var raw = await db.StringGetAsync(key).ConfigureAwait(false);
        var state = raw.HasValue
            ? JsonSerializer.Deserialize<BucketState>(raw!) ?? new BucketState(request.Capacity, now)
            : new BucketState(request.Capacity, now);

        var elapsedMinutes = Math.Max(0, (now - state.UpdatedAt).TotalMinutes);
        var refilled = Math.Min(request.Capacity, state.Tokens + elapsedMinutes * request.RefillRatePerMinute);

        string decision;
        string? reason = null;
        if (refilled >= 1)
        {
            refilled -= 1;
            decision = "send";
        }
        else if (string.Equals(request.Urgency, "high", StringComparison.OrdinalIgnoreCase))
        {
            decision = "delay";
            reason = $"Token bucket empty for {request.Channel}. Retry after next refill cycle.";
        }
        else
        {
            decision = "drop";
            reason = $"Rate limit exceeded for {request.Channel}. Non-urgent notification dropped.";
        }

        var next = new BucketState(refilled, now);
        await db.StringSetAsync(key, JsonSerializer.Serialize(next), TimeSpan.FromHours(24)).ConfigureAwait(false);
        return new NotificationRateLimitDecision(decision, reason, (int)Math.Floor(refilled));
    }
}
