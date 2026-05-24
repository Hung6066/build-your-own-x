using Hope.Agent.Domain.Tasks;

namespace Hope.Agent.Application.Tasks;

public sealed record KanbanTaskFilter(
    Guid? UserId = null,
    KanbanColumn? Column = null,
    string? PatientRef = null,
    string? AssignedTo = null,
    int Take = 100);

public interface IKanbanTaskStore
{
    Task<KanbanTask> CreateAsync(KanbanTask task, CancellationToken ct);
    Task<KanbanTask?> GetAsync(Guid id, CancellationToken ct);
    Task<IReadOnlyList<KanbanTask>> QueryAsync(KanbanTaskFilter filter, CancellationToken ct);
    Task<KanbanTask?> UpdateAsync(Guid id, Action<KanbanTask> mutate, CancellationToken ct);
    Task<bool> DeleteAsync(Guid id, CancellationToken ct);
}

public sealed class KanbanOptions
{
    public const string Section = "Kanban";
    public bool Enabled { get; set; }
}
