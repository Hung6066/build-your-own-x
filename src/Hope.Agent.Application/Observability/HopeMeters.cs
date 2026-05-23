using System.Diagnostics.Metrics;

namespace Hope.Agent.Application.Observability;

/// <summary>
/// Central registry of OpenTelemetry meters and instruments emitted by Hope.Agent.
/// Names are dotted, lower-case, snake-cased per Prometheus conventions.
/// </summary>
public static class HopeMeters
{
    public const string MeterName = "Hope.Agent";

    public static readonly Meter Meter = new(MeterName, "1.0.0");

    public static readonly Counter<long> LlmPromptTokens =
        Meter.CreateCounter<long>("hope_llm_prompt_tokens", unit: "tokens", description: "Total prompt tokens by provider/model.");

    public static readonly Counter<long> LlmCompletionTokens =
        Meter.CreateCounter<long>("hope_llm_completion_tokens", unit: "tokens", description: "Total completion tokens by provider/model.");

    public static readonly Counter<long> AgentRuns =
        Meter.CreateCounter<long>("hope_agent_runs_total", description: "Total agent.run invocations by outcome.");

    public static readonly Histogram<double> AgentRunDurationMs =
        Meter.CreateHistogram<double>("hope_agent_run_duration_ms", unit: "ms", description: "Agent run wall-clock duration.");

    public static readonly Counter<long> ToolErrors =
        Meter.CreateCounter<long>("hope_tool_errors_total", description: "Tool execution failures by tool name.");

    public static readonly Counter<long> WorkflowsStarted =
        Meter.CreateCounter<long>("hope_workflows_started_total", description: "Workflow starts by workflow type.");

    public static readonly Counter<long> PromptShieldBlocks =
        Meter.CreateCounter<long>("hope_prompt_shield_blocks_total", description: "Prompt-injection attempts detected/blocked.");

    public static readonly Counter<long> FeedbackRecorded =
        Meter.CreateCounter<long>("hope_feedback_total", description: "User feedback events by rating.");

    public static readonly Counter<long> SkillHits =
        Meter.CreateCounter<long>("hope_skill_hits_total", description: "Learned-skill retrievals applied to agent runs.");

    public static readonly Counter<long> RouterChoices =
        Meter.CreateCounter<long>("hope_router_choices_total", description: "Adaptive-router selections by intent/provider.");

    public static readonly Histogram<double> JudgeScore =
        Meter.CreateHistogram<double>("hope_judge_score", description: "Distribution of LLM-as-judge scores.");

    public static readonly Counter<long> ReflectionRevisions =
        Meter.CreateCounter<long>("hope_reflection_revisions_total", description: "Reflector self-critique revisions accepted.");

    public static readonly Counter<long> ShadowComparisons =
        Meter.CreateCounter<long>("hope_shadow_comparisons_total", description: "Champion-vs-challenger shadow runs.");

    public static readonly Counter<long> ChallengerPromotions =
        Meter.CreateCounter<long>("hope_challenger_promotions_total", description: "Challenger models promoted after winning A/B.");

    public static readonly Counter<long> AdversarialPromotions =
        Meter.CreateCounter<long>("hope_adversarial_promotions_total", description: "Auto-promoted adversarial signatures.");

    public static readonly Counter<long> KgEntitiesIngested =
        Meter.CreateCounter<long>("hope_kg_entities_total", description: "Knowledge-graph entities written by extraction pipeline.");

    public static readonly Counter<long> KgRelationsIngested =
        Meter.CreateCounter<long>("hope_kg_relations_total", description: "Knowledge-graph relations written by extraction pipeline.");
}
