using Hope.Agent.Application.Learning;
using Hope.Agent.Domain.Learning;
using Hope.Agent.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;

namespace Hope.Agent.Infrastructure.Learning;

internal sealed class EfFeedbackStore(AgentDbContext db) : IFeedbackStore
{
    public async Task RecordAsync(Feedback feedback, CancellationToken ct)
    {
        await db.Feedback.AddAsync(feedback, ct);
        await db.SaveChangesAsync(ct);
    }

    public async Task<IReadOnlyList<Feedback>> RecentByConversationAsync(Guid conversationId, int take, CancellationToken ct)
    {
        return await db.Feedback.AsNoTracking()
            .Where(f => f.ConversationId == conversationId)
            .OrderByDescending(f => f.CreatedAt)
            .Take(take)
            .ToListAsync(ct);
    }
}

internal sealed class EfSkillLibrary(AgentDbContext db) : ISkillLibrary
{
    private const double Alpha = 0.2; // EMA factor

    public async Task RecordSuccessAsync(LearnedSkill skill, CancellationToken ct)
    {
        var existing = await db.LearnedSkills.FirstOrDefaultAsync(s => s.Signature == skill.Signature, ct);
        if (existing is null)
        {
            await db.LearnedSkills.AddAsync(skill, ct);
        }
        else
        {
            existing.UsageCount += 1;
            existing.LastUsed = skill.LastUsed;
            existing.Reward = (1 - Alpha) * existing.Reward + Alpha * skill.Reward;
        }
        await db.SaveChangesAsync(ct);
    }

    public async Task<IReadOnlyList<LearnedSkill>> RetrieveByIntentAsync(string intent, int topK, CancellationToken ct)
    {
        return await db.LearnedSkills.AsNoTracking()
            .Where(s => s.Intent == intent && s.Reward > 0)
            .OrderByDescending(s => s.Reward)
            .ThenByDescending(s => s.UsageCount)
            .Take(topK)
            .ToListAsync(ct);
    }

    public async Task IncrementUsageAsync(Guid skillId, double rewardDelta, CancellationToken ct)
    {
        var skill = await db.LearnedSkills.FirstOrDefaultAsync(s => s.Id == skillId, ct);
        if (skill is null) return;
        skill.UsageCount += 1;
        skill.LastUsed = DateTimeOffset.UtcNow;
        skill.Reward = (1 - Alpha) * skill.Reward + Alpha * rewardDelta;
        await db.SaveChangesAsync(ct);
    }
}
