namespace Hope.Agent.Application.Tools;

public sealed record NotificationRateLimitRequest(
    string PatientId,
    string Channel,
    string Urgency,
    int Capacity,
    int RefillRatePerMinute);

public sealed record NotificationRateLimitDecision(
    string Decision,
    string? Reason,
    int TokensRemaining);

public interface INotificationRateLimiter
{
    Task<NotificationRateLimitDecision> DecideAsync(NotificationRateLimitRequest request, CancellationToken ct);
}
