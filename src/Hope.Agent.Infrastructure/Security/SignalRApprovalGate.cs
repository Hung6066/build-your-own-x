using System.Collections.Concurrent;
using Hope.Agent.Application.Notifications;
using Hope.Agent.Application.Observability;
using Hope.Agent.Application.Security;
using Hope.Agent.Domain.Security;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace Hope.Agent.Infrastructure.Security;

/// <summary>
/// Approval gate that persists requests to EF, pushes a realtime "approval_required" notification
/// to connected clinicians, and awaits an out-of-band decision via REST (which calls <see cref="CompleteAsync"/>).
/// Defaults to deny on timeout — "silence is not consent".
/// </summary>
internal sealed class SignalRApprovalGate(
    IRealtimeNotifier notifier,
    IServiceScopeFactory scopeFactory,
    IOptionsMonitor<ToolApprovalOptions> opts,
    ILogger<SignalRApprovalGate> log) : IToolApprovalGate
{
    private readonly ConcurrentDictionary<Guid, TaskCompletionSource<ApprovalResult>> _pending = new();

    public async Task<ApprovalResult> RequestAsync(ApprovalRequestInput input, CancellationToken ct)
    {
        var id = Guid.CreateVersion7();
        var record = new ToolApprovalRequest
        {
            Id = id,
            ConversationId = input.ConversationId,
            UserId = input.UserId,
            ToolName = input.ToolName,
            ArgumentsJson = input.ArgumentsJson,
            Impact = input.Impact,
            RequestedAt = DateTimeOffset.UtcNow,
            Status = ToolApprovalStatus.Pending,
        };

        await using (var scope = scopeFactory.CreateAsyncScope())
        {
            var store = scope.ServiceProvider.GetRequiredService<IToolApprovalRequestStore>();
            await store.AddAsync(record, ct);
        }

        var tcs = new TaskCompletionSource<ApprovalResult>(TaskCreationOptions.RunContinuationsAsynchronously);
        _pending[id] = tcs;

        var timeoutSeconds = Math.Max(1, opts.CurrentValue.TimeoutSeconds);
        HopeMeters.ToolApprovalsRequested.Add(1,
            new KeyValuePair<string, object?>("tool", input.ToolName),
            new KeyValuePair<string, object?>("impact", input.Impact.ToString()));

        try
        {
            var notification = new AgentNotification(
                Id: id,
                OccurredAt: record.RequestedAt,
                Channel: "approvals",
                Type: "approval_required",
                Title: $"Approval required: {input.ToolName}",
                Body: $"Tool '{input.ToolName}' ({input.Impact}) requested by user {input.UserId:N}. Decide within {timeoutSeconds}s.",
                UserId: null,
                Metadata: new Dictionary<string, string>
                {
                    ["approvalId"] = id.ToString(),
                    ["tool"] = input.ToolName,
                    ["impact"] = input.Impact.ToString(),
                    ["arguments"] = input.ArgumentsJson,
                    ["conversationId"] = input.ConversationId.ToString(),
                });
            await notifier.BroadcastAsync(notification, ct);
        }
        catch (Exception ex)
        {
            log.LogWarning(ex, "Failed to broadcast approval request {Id}", id);
        }

        using var timeoutCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        timeoutCts.CancelAfter(TimeSpan.FromSeconds(timeoutSeconds));

        try
        {
            using (timeoutCts.Token.Register(() => tcs.TrySetCanceled(timeoutCts.Token)))
            {
                return await tcs.Task.ConfigureAwait(false);
            }
        }
        catch (OperationCanceledException)
        {
            _pending.TryRemove(id, out _);
            var timedOut = new ApprovalResult(false, "approval_timeout", null, ToolApprovalStatus.TimedOut);
            await PersistDecisionAsync(id, timedOut, CancellationToken.None);
            HopeMeters.ToolApprovalsTimedOut.Add(1, new KeyValuePair<string, object?>("tool", input.ToolName));
            return timedOut;
        }
    }

    public async Task<bool> CompleteAsync(Guid requestId, bool approved, string? reason, Guid decidedBy, CancellationToken ct)
    {
        var result = new ApprovalResult(
            approved,
            reason,
            decidedBy,
            approved ? ToolApprovalStatus.Approved : ToolApprovalStatus.Denied);

        await PersistDecisionAsync(requestId, result, ct);

        if (_pending.TryRemove(requestId, out var tcs))
        {
            tcs.TrySetResult(result);
            if (approved) HopeMeters.ToolApprovalsGranted.Add(1);
            else HopeMeters.ToolApprovalsDenied.Add(1);
            return true;
        }

        // Late decision — request already timed out or completed; persisted for audit but no waiter.
        return false;
    }

    private async Task PersistDecisionAsync(Guid id, ApprovalResult result, CancellationToken ct)
    {
        try
        {
            await using var scope = scopeFactory.CreateAsyncScope();
            var store = scope.ServiceProvider.GetRequiredService<IToolApprovalRequestStore>();
            var rec = await store.GetAsync(id, ct);
            if (rec is null || rec.Status != ToolApprovalStatus.Pending) return;
            rec.Status = result.Status;
            rec.DecidedAt = DateTimeOffset.UtcNow;
            rec.DecidedBy = result.DecidedBy;
            rec.Reason = result.Reason;
            await store.UpdateAsync(rec, ct);
        }
        catch (Exception ex)
        {
            log.LogWarning(ex, "Failed to persist approval decision for {Id}", id);
        }
    }
}
