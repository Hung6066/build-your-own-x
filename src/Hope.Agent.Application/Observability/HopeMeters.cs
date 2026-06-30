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

    public static readonly Counter<double> LlmCostUsd =
        Meter.CreateCounter<double>("hope_llm_cost_usd", unit: "usd", description: "Estimated LLM spend in USD by provider/model.");

    public static readonly Counter<long> SemanticCacheHits =
        Meter.CreateCounter<long>("hope_semantic_cache_hits_total", description: "Semantic chat-cache hits avoiding an LLM call.");

    public static readonly Counter<long> ToolCacheHits =
        Meter.CreateCounter<long>("hope_tool_cache_hits_total", description: "Tool-result cache hits by tool name.");

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
    public static readonly Counter<long> ToolApprovalsRequested =
        Meter.CreateCounter<long>("hope_tool_approvals_requested_total", description: "Tool invocations that required human approval, by tool and impact level.");

    public static readonly Counter<long> ToolApprovalsGranted =
        Meter.CreateCounter<long>("hope_tool_approvals_granted_total", description: "Tool approval decisions that allowed execution.");

    public static readonly Counter<long> ToolApprovalsDenied =
        Meter.CreateCounter<long>("hope_tool_approvals_denied_total", description: "Tool approval decisions that blocked execution.");

    public static readonly Counter<long> ToolApprovalsTimedOut =
        Meter.CreateCounter<long>("hope_tool_approvals_timed_out_total", description: "Tool approval requests that defaulted to deny after timeout.");

    public static readonly Counter<long> SlashCommandsExecuted =
        Meter.CreateCounter<long>("hope_slash_commands_total", description: "Slash commands handled before agent runtime, by command name.");

    public static readonly Counter<long> ConversationsCompressed =
        Meter.CreateCounter<long>("hope_conversations_compressed_total", description: "Conversations whose older turns were LLM-summarized in place.");

    public static readonly Counter<long> UserModelExtractions =
        Meter.CreateCounter<long>("hope_user_model_extractions_total", description: "Per-user trait extraction passes that produced a snapshot.");

    public static readonly Counter<long> SessionSummariesGenerated =
        Meter.CreateCounter<long>("hope_session_summaries_total", description: "Weekly session summaries written by the insights summarizer.");

    public static readonly Counter<long> SkillsRevised =
        Meter.CreateCounter<long>("hope_skill_revisions_total", description: "Learned-skill templates revised by the self-improvement loop.");

    public static readonly Counter<long> SubagentFanOuts =
        Meter.CreateCounter<long>("hope_subagent_fanouts_total", description: "Parallel sub-agent fan-out invocations.");

    public static readonly Counter<long> SpeechTranscribed =
        Meter.CreateCounter<long>("hope_speech_transcribed_total", description: "Audio inputs successfully transcribed.");

    public static readonly Counter<long> SpeechSynthesized =
        Meter.CreateCounter<long>("hope_speech_synthesized_total", description: "Text-to-speech responses generated.");

    public static readonly Counter<long> TrajectoriesExported =
        Meter.CreateCounter<long>("hope_trajectories_exported_total", description: "Conversation trajectories exported to JSONL for fine-tuning.");

    public static readonly Counter<long> BlockedToolCalls =
        Meter.CreateCounter<long>("hope_security_blocked_tool_calls_total", description: "Tool calls blocked by policy, sandbox, kill switch, or access control.");

    public static readonly Counter<long> PolicyDenials =
        Meter.CreateCounter<long>("hope_security_policy_denials_total", description: "Policy-as-code deny decisions by rule id.");

    public static readonly Counter<long> PromptInjectionDetected =
        Meter.CreateCounter<long>("hope_security_prompt_injection_detected_total", description: "Prompt injection attempts detected by input, retrieval, or tool-output rails.");

    public static readonly Counter<long> PhiRedactionCount =
        Meter.CreateCounter<long>("hope_security_phi_redactions_total", description: "PHI or secret redactions before external egress.");

    public static readonly Counter<long> CrossTenantAccessDenied =
        Meter.CreateCounter<long>("hope_security_cross_tenant_access_denied_total", description: "Cross-tenant authorization/RLS attempts denied.");

    public static readonly Counter<long> SuspiciousAutonomyActions =
        Meter.CreateCounter<long>("hope_security_suspicious_autonomy_actions_total", description: "Autonomous actions flagged by risk policy, drift, or safety budget.");

    public static readonly Counter<long> DataPerimeterDenials =
        Meter.CreateCounter<long>("hope_security_data_perimeter_denials_total", description: "Data residency, classification, or purpose-based access denials.");

    public static readonly Counter<long> ModelRoutingPolicyBlocks =
        Meter.CreateCounter<long>("hope_security_model_routing_blocks_total", description: "Model routing choices blocked by tenant, risk, PHI, or residency policy.");

    public static readonly Counter<long> BreakGlassAccesses =
        Meter.CreateCounter<long>("hope_security_break_glass_access_total", description: "Break-glass accesses requiring post-incident review.");

    public static readonly Counter<long> SecurityIncidentsOpened =
        Meter.CreateCounter<long>("hope_security_incidents_opened_total", description: "Security incidents opened by type and severity.");

    public static readonly Counter<long> AdversarialSimulationRuns =
        Meter.CreateCounter<long>("hope_security_adversarial_simulation_runs_total", description: "Continuous red-team simulation runs by result.");

    public static readonly Counter<long> SecurityShieldFailures =
        Meter.CreateCounter<long>("hope_security_shield_failures_total", description: "Security shield failures that triggered fail-open/fallback behavior.");
}
