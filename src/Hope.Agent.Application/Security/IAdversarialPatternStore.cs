using Hope.Agent.Domain.Security;

namespace Hope.Agent.Application.Security;

public interface IAdversarialPatternStore
{
    Task<AdversarialPattern> ObserveAsync(string sample, string reason, CancellationToken ct);
    Task<IReadOnlyList<AdversarialPattern>> ActivePatternsAsync(CancellationToken ct);
    Task<IReadOnlyList<AdversarialPattern>> AllAsync(int take, CancellationToken ct);
    Task PromoteAsync(Guid id, CancellationToken ct);
    Task DemoteAsync(Guid id, CancellationToken ct);
}
