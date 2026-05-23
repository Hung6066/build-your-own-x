using Hope.Agent.Domain.Conversations;

namespace Hope.Agent.Application.Abstractions;

public interface IConversationRepository
{
    Task<Conversation?> GetAsync(Guid id, CancellationToken ct);
    Task AddAsync(Conversation conversation, CancellationToken ct);
    Task SaveChangesAsync(CancellationToken ct);
}
