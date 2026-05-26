using Hope.Agent.Application.Agents;
using Hope.Agent.Application.Learning;
using Hope.Agent.Application.Tools;
using Hope.Agent.Domain.Learning;
using Microsoft.Extensions.Logging;

namespace Hope.Agent.MultiAgent.Learning;

/// <summary>
/// Records workflow dispatch outcomes into the learning system.
/// <para>
/// Every time a workflow step completes (success or failure), this sink:
/// <list type="bullet">
///   <item>Appends a <see cref="Feedback"/> record (rating +1 / -1) to the feedback store.</item>
///   <item>On success: reinforces the matching <see cref="LearnedSkill"/> via EMA reward update.</item>
/// </list>
/// This closes the feedback loop: real-world outcomes gradually shift the bandit weights
/// in <see cref="IAdaptiveRouter"/> and the skill confidence in <see cref="ISkillLibrary"/>.
/// </para>
/// </summary>
internal sealed class WorkflowOutcomeSink(
    ISkillLibrary skillLibrary,
    IFeedbackStore feedbackStore,
    IOptimizationCostHints? costHints,
    ILogger<WorkflowOutcomeSink> log) : IWorkflowOutcomeSink
{
    public async Task RecordAsync(WorkflowOutcome outcome, CancellationToken ct)
    {
        try
        {
            await feedbackStore.RecordAsync(new Feedback
            {
                Id = Guid.CreateVersion7(),
                UserId = Guid.Empty,
                ConversationId = Guid.Empty,
                Rating = outcome.Success ? 1 : -1,
                Comment = $"workflow:{outcome.WorkflowType} role:{outcome.Role}",
                Intent = outcome.Intent,
                CreatedAt = DateTimeOffset.UtcNow,
            }, ct);

            // Reinforce successful patterns so future identical intents converge faster
            if (outcome.Success && outcome.RewardSignal > 0)
            {
                var skills = await skillLibrary.RetrieveByIntentAsync(outcome.Intent, 1, ct);
                if (skills.Count > 0)
                    await skillLibrary.IncrementUsageAsync(skills[0].Id, outcome.RewardSignal, ct);
            }

            // Update MCMF adaptive costs for booking-related intents
            if (costHints is not null &&
                outcome.Context is { Count: > 0 } ctx &&
                (outcome.Intent is "his_booking" or "optimize_slots" or "schedule"))
            {
                var doctorId = ctx.GetValueOrDefault("doctor_id", ctx.GetValueOrDefault("doctor", "unknown"));
                var specialty = ctx.GetValueOrDefault("specialty", "unknown");
                await costHints.RecordOutcomeAsync(doctorId, specialty, outcome.Success, ct);
            }
        }
        catch (Exception ex)
        {
            // Non-fatal: learning updates must not break the critical workflow path
            log.LogWarning(ex, "WorkflowOutcomeSink failed for intent={Intent} workflow={Workflow}",
                outcome.Intent, outcome.WorkflowType);
        }
    }
}
