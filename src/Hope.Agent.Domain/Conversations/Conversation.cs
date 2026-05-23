namespace Hope.Agent.Domain.Conversations;

public sealed class Conversation
{
    public Guid Id { get; private set; }
    public Guid UserId { get; private set; }
    public string Title { get; private set; } = string.Empty;
    public DateTimeOffset CreatedAt { get; private set; }
    public DateTimeOffset UpdatedAt { get; private set; }
    public List<ConversationMessage> Messages { get; private set; } = [];

    private Conversation() { }

    public static Conversation Create(Guid userId, string title, DateTimeOffset now) => new()
    {
        Id = Guid.CreateVersion7(),
        UserId = userId,
        Title = title,
        CreatedAt = now,
        UpdatedAt = now,
    };

    public ConversationMessage AddMessage(MessageRole role, string content, DateTimeOffset now, string? toolName = null, string? toolCallId = null)
    {
        var msg = new ConversationMessage
        {
            Id = Guid.CreateVersion7(),
            ConversationId = Id,
            Role = role,
            Content = content,
            ToolName = toolName,
            ToolCallId = toolCallId,
            CreatedAt = now,
        };
        Messages.Add(msg);
        UpdatedAt = now;
        return msg;
    }
}

public sealed class ConversationMessage
{
    public Guid Id { get; init; }
    public Guid ConversationId { get; init; }
    public MessageRole Role { get; init; }
    public string Content { get; init; } = string.Empty;
    public string? ToolName { get; init; }
    public string? ToolCallId { get; init; }
    public int? PromptTokens { get; init; }
    public int? CompletionTokens { get; init; }
    public DateTimeOffset CreatedAt { get; init; }
}

public enum MessageRole
{
    System = 0,
    User = 1,
    Assistant = 2,
    Tool = 3,
}
