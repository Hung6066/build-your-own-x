using Hope.Agent.Application.Abstractions;
using Hope.Agent.Domain.Conversations;
using Microsoft.EntityFrameworkCore;

namespace Hope.Agent.Infrastructure.Persistence;

internal sealed class EfConversationRepository(AgentDbContext db) : IConversationRepository
{
    public Task<Conversation?> GetAsync(Guid id, CancellationToken ct) =>
        db.Conversations.Include(c => c.Messages).FirstOrDefaultAsync(c => c.Id == id, ct);

    public async Task AddAsync(Conversation conversation, CancellationToken ct) =>
        await db.Conversations.AddAsync(conversation, ct);

    public Task SaveChangesAsync(CancellationToken ct) => db.SaveChangesAsync(ct);
}
