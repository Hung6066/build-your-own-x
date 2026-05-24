using Hope.Agent.Application.Tasks;
using Hope.Agent.Domain.Tasks;
using Hope.Agent.Infrastructure.Persistence;
using Hope.Agent.Shared;
using Microsoft.EntityFrameworkCore;

namespace Hope.Agent.Infrastructure.Tasks;

internal sealed class EfKanbanTaskStore(AgentDbContext db, IClock clock) : IKanbanTaskStore
{
    public async Task<KanbanTask> CreateAsync(KanbanTask task, CancellationToken ct)
    {
        var now = clock.UtcNow;
        var entity = new KanbanTask
        {
            Id = task.Id == Guid.Empty ? Guid.CreateVersion7() : task.Id,
            UserId = task.UserId,
            ConversationId = task.ConversationId,
            PatientRef = task.PatientRef,
            Title = task.Title,
            Description = task.Description,
            Column = task.Column,
            Priority = task.Priority,
            CreatedAt = now,
            UpdatedAt = now,
            DueAt = task.DueAt,
            AssignedTo = task.AssignedTo,
            Tags = task.Tags,
        };
        db.KanbanTasks.Add(entity);
        await db.SaveChangesAsync(ct);
        return entity;
    }

    public Task<KanbanTask?> GetAsync(Guid id, CancellationToken ct) =>
        db.KanbanTasks.FirstOrDefaultAsync(x => x.Id == id, ct);

    public async Task<IReadOnlyList<KanbanTask>> QueryAsync(KanbanTaskFilter filter, CancellationToken ct)
    {
        var q = db.KanbanTasks.AsNoTracking().AsQueryable();
        if (filter.UserId is Guid u) q = q.Where(x => x.UserId == u);
        if (filter.Column is KanbanColumn c) q = q.Where(x => x.Column == c);
        if (!string.IsNullOrWhiteSpace(filter.PatientRef)) q = q.Where(x => x.PatientRef == filter.PatientRef);
        if (!string.IsNullOrWhiteSpace(filter.AssignedTo)) q = q.Where(x => x.AssignedTo == filter.AssignedTo);
        return await q.OrderByDescending(x => x.UpdatedAt)
            .Take(Math.Clamp(filter.Take, 1, 500))
            .ToListAsync(ct);
    }

    public async Task<KanbanTask?> UpdateAsync(Guid id, Action<KanbanTask> mutate, CancellationToken ct)
    {
        var entity = await db.KanbanTasks.FirstOrDefaultAsync(x => x.Id == id, ct);
        if (entity is null) return null;
        mutate(entity);
        entity.UpdatedAt = clock.UtcNow;
        if (entity.Column == KanbanColumn.Done && entity.CompletedAt is null)
            entity.CompletedAt = entity.UpdatedAt;
        await db.SaveChangesAsync(ct);
        return entity;
    }

    public async Task<bool> DeleteAsync(Guid id, CancellationToken ct)
    {
        var entity = await db.KanbanTasks.FirstOrDefaultAsync(x => x.Id == id, ct);
        if (entity is null) return false;
        db.KanbanTasks.Remove(entity);
        await db.SaveChangesAsync(ct);
        return true;
    }
}
