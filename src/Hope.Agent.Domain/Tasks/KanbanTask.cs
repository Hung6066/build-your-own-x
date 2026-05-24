namespace Hope.Agent.Domain.Tasks;

public enum KanbanColumn
{
    Backlog = 0,
    Todo = 1,
    InProgress = 2,
    Blocked = 3,
    Done = 4,
    Cancelled = 5,
}

public enum KanbanPriority
{
    Low = 0,
    Normal = 1,
    High = 2,
    Urgent = 3,
}

public sealed class KanbanTask
{
    public Guid Id { get; init; }
    public Guid? UserId { get; set; }
    public Guid? ConversationId { get; set; }
    public string? PatientRef { get; set; }
    public string Title { get; set; } = string.Empty;
    public string? Description { get; set; }
    public KanbanColumn Column { get; set; } = KanbanColumn.Todo;
    public KanbanPriority Priority { get; set; } = KanbanPriority.Normal;
    public DateTimeOffset CreatedAt { get; init; }
    public DateTimeOffset UpdatedAt { get; set; }
    public DateTimeOffset? DueAt { get; set; }
    public DateTimeOffset? CompletedAt { get; set; }
    public string? AssignedTo { get; set; }
    public string? Tags { get; set; }
}
