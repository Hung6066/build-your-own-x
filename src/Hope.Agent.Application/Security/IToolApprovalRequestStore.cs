using Hope.Agent.Domain.Security;

namespace Hope.Agent.Application.Security;

public interface IToolApprovalRequestStore
{
    Task AddAsync(ToolApprovalRequest request, CancellationToken ct);
    Task<ToolApprovalRequest?> GetAsync(Guid id, CancellationToken ct);
    Task<IReadOnlyList<ToolApprovalRequest>> PendingAsync(int take, CancellationToken ct);
    Task<IReadOnlyList<ToolApprovalRequest>> QueryAsync(DateTimeOffset from, DateTimeOffset to, int take, CancellationToken ct);
    Task UpdateAsync(ToolApprovalRequest request, CancellationToken ct);
}
