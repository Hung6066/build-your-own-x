using Hope.Agent.Application.LLM;
using Hope.Agent.Application.Observability;
using Hope.Agent.Domain.Learning;
using Hope.Agent.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace Hope.Agent.Infrastructure.Learning;

public sealed class SkillSelfImprovementOptions
{
    public const string Section = "SkillSelfImprovement";
    public bool Enabled { get; set; }
    public double RewardThreshold { get; set; } = 0.7;
    public int MinUsage { get; set; } = 5;
    public int MaxRevisionsPerRun { get; set; } = 20;
    public int IntervalHours { get; set; } = 24;
    public int RunHourUtc { get; set; } = 3;
}

/// <summary>
/// Daily pass that finds learned skills with low reward (after enough usage) and asks an LLM
/// to rewrite their <c>AnswerTemplate</c>, then writes the revision back via raw SQL so we
/// don't depend on the entity exposing a public setter.
/// </summary>
internal sealed class SkillSelfImprovementHostedService(
    IServiceScopeFactory scopes,
    IOptions<SkillSelfImprovementOptions> opts,
    ILogger<SkillSelfImprovementHostedService> log) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        var o = opts.Value;
        if (!o.Enabled)
        {
            log.LogInformation("Skill self-improvement disabled.");
            return;
        }
        log.LogInformation("Skill self-improvement started (every {Hours}h at {Hour:00}:00 UTC).",
            o.IntervalHours, o.RunHourUtc);

        DateTimeOffset? lastRun = null;
        while (!stoppingToken.IsCancellationRequested)
        {
            try { await Task.Delay(TimeSpan.FromHours(1), stoppingToken); }
            catch (OperationCanceledException) { return; }

            var now = DateTimeOffset.UtcNow;
            if (now.Hour != o.RunHourUtc) continue;
            if (lastRun is { } prev && (now - prev).TotalHours < o.IntervalHours) continue;
            lastRun = now;

            try { await RunOnceAsync(o, stoppingToken); }
            catch (Exception ex) { log.LogError(ex, "Skill self-improvement pass failed"); }
        }
    }

    private async Task RunOnceAsync(SkillSelfImprovementOptions o, CancellationToken ct)
    {
        await using var scope = scopes.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<AgentDbContext>();
        var llm = scope.ServiceProvider.GetRequiredService<ILLMRouter>();

        var candidates = await db.LearnedSkills
            .Where(s => s.UsageCount >= o.MinUsage && s.Reward < o.RewardThreshold)
            .OrderBy(s => s.Reward)
            .Take(o.MaxRevisionsPerRun)
            .ToListAsync(ct);
        if (candidates.Count == 0)
        {
            log.LogInformation("Skill self-improvement: no candidates.");
            return;
        }

        log.LogInformation("Skill self-improvement: revising {Count} skill(s).", candidates.Count);
        foreach (var skill in candidates)
        {
            try
            {
                var revised = await ReviseAsync(llm, skill, ct);
                if (string.IsNullOrWhiteSpace(revised) ||
                    string.Equals(revised, skill.AnswerTemplate, StringComparison.Ordinal))
                    continue;

                // AnswerTemplate is init-only on the domain entity; update via the change-tracker
                // property bag so we don't need to widen the setter.
                db.Entry(skill).Property(nameof(LearnedSkill.AnswerTemplate)).CurrentValue = revised;
                skill.LastUsed = DateTimeOffset.UtcNow;
                HopeMeters.SkillsRevised.Add(1);
            }
            catch (Exception ex)
            {
                log.LogWarning(ex, "Skill revision failed for skill {Id}", skill.Id);
            }
        }
        await db.SaveChangesAsync(ct);
    }

    private static async Task<string?> ReviseAsync(ILLMRouter llm, LearnedSkill skill, CancellationToken ct)
    {
        var sys = "You revise a learned-skill answer template for a clinical assistant. " +
                  "Goal: improve clarity, safety, and adherence to clinician intent. " +
                  "Preserve placeholders like {variable}. Output ONLY the revised template text, no commentary.";
        var user =
            $"Intent: {skill.Intent}\n" +
            $"Signature: {skill.Signature}\n" +
            $"Usage count: {skill.UsageCount}\n" +
            $"Current reward (0..1): {skill.Reward:F2}\n\n" +
            $"--- CURRENT TEMPLATE ---\n{skill.AnswerTemplate}\n--- END ---\n\n" +
            "Propose a revised template.";

        var resp = await llm.SelectChat().CompleteAsync(new ChatRequest(
            [new ChatMessage("system", sys), new ChatMessage("user", user)],
            Temperature: 0.2f,
            MaxTokens: 600), ct);
        return resp.Content.Trim();
    }
}
