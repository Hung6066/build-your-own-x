using System.Diagnostics;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Hope.Agent.Application.Abstractions;
using Hope.Agent.Application.Autonomy;
using Hope.Agent.Application.Agents;
using Hope.Agent.Application.Compression;
using Hope.Agent.Application.Context;
using Hope.Agent.Application.Knowledge;
using Hope.Agent.Application.Locking;
using Hope.Agent.Application.LLM;
using Hope.Agent.Application.Learning;
using Hope.Agent.Application.Memory;
using Hope.Agent.Application.Observability;
using Hope.Agent.Application.Security;
using Hope.Agent.Application.Tools;
using Hope.Agent.Application.UserModeling;
using Hope.Agent.Domain.Audit;
using Hope.Agent.Domain.Autonomy;
using Hope.Agent.Domain.Conversations;
using Hope.Agent.Domain.Learning;
using Hope.Agent.Domain.Memory;
using Hope.Agent.Domain.Security;
using Hope.Agent.Shared;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace Hope.Agent.AgentRuntime;

public sealed class AgentRuntimeOptions
{
    public int MaxToolIterations { get; set; } = 6;
    public int MemoryTopK { get; set; } = 5;
    public bool EnableReflection { get; set; }
    public double ReflectionThreshold { get; set; } = 0.6;
    public bool EnableAdaptiveRouting { get; set; } = true;
    public bool EnableSkillRetrieval { get; set; } = true;
    public int SkillTopK { get; set; } = 3;
    public bool EnableKnowledgeGraph { get; set; } = true;
    public bool EnableShadowAB { get; set; } = true;
    /// <summary>Use hybrid dense+sparse (RRF) memory retrieval instead of dense-only. Default on.</summary>
    public bool EnableHybridRetrieval { get; set; } = true;
    /// <summary>Run an LLM reranker over retrieved memory candidates before injecting them. Default off (extra LLM call).</summary>
    public bool EnableMemoryReranking { get; set; }
    /// <summary>Candidates to fetch before reranking down to <see cref="MemoryTopK"/>.</summary>
    public int RerankCandidateK { get; set; } = 12;
    /// <summary>Use Mem0/A-Mem consolidation (fact extraction + ADD/UPDATE/DELETE) instead of raw episodic dumps. Default on.</summary>
    public bool EnableMemoryConsolidation { get; set; } = true;
    /// <summary>
    /// Soft context budget in characters. When the assembled message payload exceeds this limit,
    /// memories and skills are truncated before sending to the LLM to avoid context-window errors.
    /// Rough heuristic: 1 token ≈ 4 chars, so 32 000 chars ≈ 8K tokens.
    /// </summary>
    public int MaxContextChars { get; set; } = 32_000;
    /// <summary>Maximum consecutive tool failures before the agent aborts the tool-call loop early.</summary>
    public int MaxConsecutiveToolFailures { get; set; } = 3;
    /// <summary>Per-tool execution timeout. Tools that exceed this are cancelled.</summary>
    public TimeSpan ToolExecutionTimeout { get; set; } = TimeSpan.FromSeconds(30);
    /// <summary>When true, the agent reports degraded-mode metric when a dependency fetch failed.</summary>
    public bool TrackDegradedMode { get; set; } = true;
    public string PromptVersion { get; set; } = "hope-runtime-prompt-v1";
    public string ToolsetVersion { get; set; } = "hope-tools-v1";
    public string PolicyVersion { get; set; } = "hope-policy-v1";
    public string SystemPrompt { get; set; } =
        "You are Hope, a careful clinical operations AI for a Vietnamese healthcare provider. " +
        "Always cite which tool you used. Refuse to fabricate clinical data. " +
        "When uncertain, ask clarifying questions. Respect PHI: never echo full IDs in summaries.";
}

internal sealed class AgentOrchestrator(
    ILLMRouter router,
    IEnumerable<IChatCompletionProvider> chatProviders,
    IAdaptiveRouter adaptiveRouter,
    ISkillLibrary skillLibrary,
    IReflector reflector,
    IJudge judge,
    IShadowComparator shadow,
    IKnowledgeExtractor kgExtractor,
    IKnowledgeGraphStore kgStore,
    IToolRegistry tools,
    IToolApprovalPolicy approvalPolicy,
    IToolApprovalGate approvalGate,
    IToolAccessPolicy accessPolicy,
    IOutputShield outputShield,
    IRetrievalRail retrievalRail,
    IToolExecutor sandbox,
    IConversationRepository convRepo,
    IMemoryStore memory,
    IConversationCompressor compressor,
    IUserModelService userModel,
    IAuditSink audit,
    IPromptShield shield,
    IPhiRedactor phi,
    IPromptEgressGuard egressGuard,
    IClock clock,
    IOptions<AgentRuntimeOptions> opts,
    ILogger<AgentOrchestrator> log,
    AgentRuntimeOptionalServices optional) : IAgentRuntime
{
    private static readonly ActivitySource Activity = new("Hope.Agent.Runtime");
    private readonly AgentRuntimeOptions _opts = opts.Value;
    private readonly Dictionary<string, IChatCompletionProvider> _chatByName =
        chatProviders.ToDictionary(p => p.Name, StringComparer.OrdinalIgnoreCase);
    /// <summary>Tracks consecutive failures per tool name for circuit-breaker (I-04).</summary>
    private readonly Dictionary<string, int> _toolFailureCount = new(StringComparer.OrdinalIgnoreCase);

    public async Task<AgentResponse> RunAsync(AgentRequest request, CancellationToken ct)
    {
        using var activity = Activity.StartActivity("agent.run");
        activity?.SetTag("user.id", request.UserId);
        var sw = Stopwatch.StartNew();
        var now = clock.UtcNow;

        // ── M-01: wrap shield in try-catch — if the shield itself throws (e.g. Redis timeout
        //    for adversarial pattern store), fail open so output shield + egress guard still protect.
        PromptShieldResult inspection;
        try { inspection = shield.Inspect(request.Message); }
        catch (Exception ex)
        {
            log.LogWarning(ex, "PromptShield threw; failing open — output shield still protects");
            if (_opts.TrackDegradedMode)
                HopeMeters.AgentRuns.Add(1, new KeyValuePair<string, object?>("outcome", "degraded_shield"));
            HopeMeters.SecurityShieldFailures.Add(1,
                new KeyValuePair<string, object?>("shield", "prompt"),
                new KeyValuePair<string, object?>("mode", "fail_open"));
            inspection = new PromptShieldResult(Allowed: true, SanitizedInput: request.Message, Reasons: []);
        }
        if (!inspection.Allowed)
        {
            HopeMeters.AgentRuns.Add(1, new KeyValuePair<string, object?>("outcome", "blocked"));
            log.LogWarning("Prompt blocked by shield: {Reasons}", string.Join(",", inspection.Reasons));
            await audit.WriteAsync(new AuditEvent
            {
                Id = Guid.CreateVersion7(),
                OccurredAt = now,
                UserId = request.UserId,
                Actor = "agent_runtime",
                Action = "agent.blocked",
                ResourceType = "prompt",
                ResourceId = null,
                CorrelationId = request.CorrelationId,
                PayloadJson = JsonSerializer.Serialize(new { reasons = inspection.Reasons }),
            }, ct);
            throw new InvalidOperationException("Request blocked by safety policy.");
        }
        request = request with { Message = inspection.SanitizedInput };

        var conv = await LoadOrCreateConversationAsync(request, now, ct);
        conv.AddMessage(MessageRole.User, request.Message, now);

        var intent = InferIntent(request);

        // Context gathering is intentionally sequential here to avoid concurrent
        // access to scoped EF DbContext-backed services within the same request.
        var memories = await RetrieveMemoriesAsync(request, ct);
        var skillHits = _opts.EnableSkillRetrieval
            ? await SafeRetrieveSkillsAsync(intent, ct)
            : Array.Empty<LearnedSkill>();
        UserTraitsSnapshot? traits = await SafeGetUserTraitsAsync(request.UserId, ct);
        CompressionResult? compression = await SafeCompressAsync(conv, ct);
        ClinicalContextFragment? clinicalFragment = optional.ClinicalContext is not null
            ? await SafeGetClinicalContextAsync(optional.ClinicalContext, request.AgentProfile, ct)
            : null;
        var messages = EnforceContextBudget(BuildMessages(conv, memories, skillHits, traits, compression, clinicalFragment), _opts.MaxContextChars);

        IChatCompletionProvider chat;
        RouterChoice? adaptiveChoice = null;
        if (_opts.EnableAdaptiveRouting)
        {
            adaptiveChoice = await adaptiveRouter.SelectChatAsync(intent, ct);
            chat = _chatByName.TryGetValue(adaptiveChoice.Provider, out var picked) ? picked : router.SelectChat();
            HopeMeters.RouterChoices.Add(1,
                new KeyValuePair<string, object?>("intent", intent),
                new KeyValuePair<string, object?>("provider", chat.Name));
        }
        else
        {
            chat = router.SelectChat();
        }
        var allowedToolNames = ResolveAllowedToolNames(intent);
        var toolDefs = tools.All
            .Where(t => allowedToolNames.Count == 0 || allowedToolNames.Contains(t.Definition.Name))
            .Select(t => t.Definition)
            .ToList();
        var toolExecutions = new List<AgentToolExecution>();

        int promptTokens = 0, completionTokens = 0;
        decimal costUsd = 0m;
        string provider = chat.Name, model = string.Empty;
        string finalContent = string.Empty;

        int consecutiveFailures = 0;
        for (int iter = 0; iter < _opts.MaxToolIterations; iter++)
        {
            // ── H-01: honour cancellation between each iteration ──
            ct.ThrowIfCancellationRequested();

            // ── I-04: early exit when tools fail repeatedly (circuit breaker) ──
            if (consecutiveFailures >= _opts.MaxConsecutiveToolFailures)
            {
                log.LogWarning("Tool loop aborted after {Count} consecutive failures", consecutiveFailures);
                finalContent = "I'm sorry, I encountered repeated errors while trying to process your request. Please try again or contact support.";
                conv.AddMessage(MessageRole.Assistant, finalContent, clock.UtcNow);
                HopeMeters.ToolErrors.Add(1, new KeyValuePair<string, object?>("tool", "circuit_breaker"));
                break;
            }

            // ── C-3: per-tenant budget check before LLM call ──
            if (optional.Billing is not null && request.TenantId is { } tid)
            {
                var withinBudget = await optional.Billing.CheckBudgetAsync(tid, chat.Name, messages.Sum(m => m.Content?.Length ?? 0) / 4, ct);
                if (!withinBudget)
                {
                    finalContent = "Your organisation's AI usage budget for this month has been reached. Please contact your administrator.";
                    conv.AddMessage(MessageRole.Assistant, finalContent, clock.UtcNow);
                    HopeMeters.AgentRuns.Add(1, new KeyValuePair<string, object?>("outcome", "budget_exceeded"));
                    break;
                }
            }

            var resp = await chat.CompleteAsync(new ChatRequest(messages, Tools: toolDefs, UserId: request.UserId.ToString()), ct);
            provider = resp.Provider;
            model = resp.Model;
            promptTokens += resp.Usage.PromptTokens;
            completionTokens += resp.Usage.CompletionTokens;
            costUsd += resp.Usage.CostUsd;

            // ── C-3: record billing usage after successful LLM call ──
            if (optional.Billing is not null && request.TenantId is { } billingTid)
            {
                try
                {
                    await optional.Billing.RecordUsageAsync(new Application.Billing.UsageRecord(
                        billingTid, request.UserId, conv.Id, resp.Provider, resp.Model, intent,
                        resp.Usage.PromptTokens, resp.Usage.CompletionTokens, resp.Usage.CostUsd, clock.UtcNow),
                        ct);
                }
                catch (Exception ex) { log.LogWarning(ex, "Billing record failed"); }
            }

            if (resp.ToolCalls.Count == 0)
            {
                finalContent = resp.Content;
                conv.AddMessage(MessageRole.Assistant, finalContent, clock.UtcNow);
                break;
            }

            messages = AppendAssistantToolCalls(messages, resp);

            // ── C-5: parallel tool execution ─────────────────────────────────
            // When multiple tool_calls are present in the same assistant response,
            // they are independent by construction (the model would split dependent
            // calls across turns). Fan them out with Task.WhenAll, then append
            // results in the original order to preserve conversation state.
            if (resp.ToolCalls.Count == 1)
            {
                // Single tool — existing sequential path
                var (output, exec) = await ExecuteToolAsync(resp.ToolCalls[0], request, conv, ct);
                toolExecutions.Add(exec);
                messages = AppendToolResult(messages, resp.ToolCalls[0], output);
                if (exec.Success) consecutiveFailures = 0;
                else consecutiveFailures++;
            }
            else
            {
                // Multiple independent tools — parallel fan-out
                var parallelResults = await Task.WhenAll(
                    resp.ToolCalls.Select(call => ExecuteToolCoreAsync(call, request, ct)));

                for (int i = 0; i < resp.ToolCalls.Count; i++)
                {
                    var call = resp.ToolCalls[i];
                    var (output, exec) = parallelResults[i];
                    toolExecutions.Add(exec);
                    messages = AppendToolResult(messages, call, output);
                    conv.AddMessage(MessageRole.Tool, output, clock.UtcNow, call.Name, call.Id);
                    if (exec.Success) consecutiveFailures = 0;
                    else consecutiveFailures++;
                }
            }
        }

        await convRepo.SaveChangesAsync(ct);

        // Persist memory updates in-scope to avoid using disposed scoped services.
        var persistConvId = conv.Id;
        var persistUserId = request.UserId;
        var persistMessage = request.Message;
        var persistContent = finalContent;
        var persistProfile = request.AgentProfile;
        try
        {
            var persistCtx = new MemoryConsolidationContext(persistUserId, persistConvId, persistMessage, persistContent, persistProfile);
            if (_opts.EnableMemoryConsolidation && optional.Consolidator is not null)
                await optional.Consolidator.ConsolidateAsync(persistCtx, ct);
            else
                await StoreEpisodicFallbackAsync(persistUserId, persistConvId, persistMessage, persistContent, persistProfile, ct);
        }
        catch (Exception ex) { log.LogWarning(ex, "Memory persistence failed"); }

        var convIdForExtract = conv.Id;
        var userIdForExtract = request.UserId;
        try
        {
            await userModel.TryExtractAsync(userIdForExtract, convIdForExtract, ct);
        }
        catch (Exception ex) { log.LogWarning(ex, "User-model extract failed"); }

        if (_opts.EnableReflection && !string.IsNullOrWhiteSpace(finalContent))
        {
            try
            {
                var refl = await reflector.CritiqueAndRefineAsync(request.Message, finalContent, ct);
                if (refl.Score < _opts.ReflectionThreshold && !string.IsNullOrWhiteSpace(refl.RefinedAnswer))
                {
                    finalContent = refl.RefinedAnswer;
                    HopeMeters.ReflectionRevisions.Add(1);
                    log.LogInformation("Reflector revised answer (score={Score:F2})", refl.Score);
                }
            }
            catch (Exception ex)
            {
                log.LogWarning(ex, "Reflector failed; using draft answer as-is");
            }
        }

        var hadToolError = toolExecutions.Any(t => !t.Success);
        if (_opts.EnableAdaptiveRouting && adaptiveChoice is not null)
        {
            double reward = hadToolError ? -0.5 : 0.2; // baseline; explicit feedback later adjusts via /v1/learning/feedback
            try { await adaptiveRouter.RecordOutcomeAsync(intent, adaptiveChoice.Provider, adaptiveChoice.Model, reward, sw.Elapsed.TotalMilliseconds, hadToolError, ct); }
            catch (Exception ex) { log.LogWarning(ex, "Failed to record router outcome"); }
        }

        if (!hadToolError && !string.IsNullOrWhiteSpace(finalContent))
        {
            await DistillSkillAsync(intent, request.Message, finalContent, toolExecutions, ct);
        }

        if (_opts.EnableKnowledgeGraph && !string.IsNullOrWhiteSpace(finalContent))
        {
            _ = IngestKnowledgeAsync(request.Message, finalContent, CancellationToken.None);
        }

        if (_opts.EnableShadowAB)
        {
            _ = RunShadowAsync(intent, chat, request, messages, finalContent, CancellationToken.None);
        }

        var providerTag = new KeyValuePair<string, object?>("provider", provider);
        var modelTag = new KeyValuePair<string, object?>("model", model);
        HopeMeters.LlmPromptTokens.Add(promptTokens, providerTag, modelTag);
        HopeMeters.LlmCompletionTokens.Add(completionTokens, providerTag, modelTag);
        if (costUsd > 0m)
        {
            HopeMeters.LlmCostUsd.Add((double)costUsd, providerTag, modelTag);
        }
        HopeMeters.AgentRuns.Add(1, new KeyValuePair<string, object?>("outcome", "ok"));
        HopeMeters.AgentRunDurationMs.Record(sw.Elapsed.TotalMilliseconds, providerTag, modelTag);

        var contextManifest = new
        {
            intent,
            agentProfile = request.AgentProfile,
            memoryRefs = memories.Select(m => new { id = m.Record.Id, kind = m.Record.Kind, score = m.Score }).ToArray(),
            skillRefs = skillHits.Select(s => new { s.Id, s.Intent, s.Reward }).ToArray(),
            hasUserTraits = traits is not null,
            hasCompression = compression is not null,
            hasClinicalContext = clinicalFragment is not null,
            contextChars = messages.Sum(m => m.Content?.Length ?? 0),
            maxContextChars = _opts.MaxContextChars,
            messageCount = messages.Count,
        };
        var versionFingerprint = new
        {
            provider,
            model,
            promptVersion = _opts.PromptVersion,
            toolsetVersion = _opts.ToolsetVersion,
            policyVersion = _opts.PolicyVersion,
            runtimeOptions = new
            {
                _opts.MaxToolIterations,
                _opts.MemoryTopK,
                _opts.EnableHybridRetrieval,
                _opts.EnableMemoryConsolidation,
                _opts.MaxContextChars,
            },
        };

        await audit.WriteAsync(new AuditEvent
        {
            Id = Guid.CreateVersion7(),
            OccurredAt = clock.UtcNow,
            UserId = request.UserId,
            Actor = "agent_runtime",
            Action = "agent.run",
            ResourceType = "conversation",
            ResourceId = conv.Id.ToString(),
            CorrelationId = request.CorrelationId,
            PayloadJson = JsonSerializer.Serialize(new
            {
                tools = toolExecutions.Select(t => t.Tool),
                provider,
                model,
                promptTokens,
                completionTokens,
                costUsd,
                redactedInput = phi.Redact(request.Message),
                contextManifest,
                versionFingerprint,
            }),
        }, ct);

        if (optional.Autonomy is not null)
        {
            try
            {
                var proposedAction = toolExecutions.Count == 0
                    ? null
                    : JsonSerializer.Serialize(toolExecutions.Select(t => new { tool = t.Tool, success = t.Success }).ToArray());
                var evaluation = optional.Autonomy.Evaluate(new AutonomyEvaluationRequest(
                    request.UserId,
                    null,
                    conv.Id,
                    intent,
                    request.AgentProfile,
                    request.Message,
                    toolExecutions.LastOrDefault()?.Tool,
                    proposedAction,
                    hadToolError ? 0.55 : 0.88,
                    request.CorrelationId));
                await optional.Autonomy.RecordDecisionAsync(new AgentDecisionWrite(
                    request.UserId,
                    null,
                    conv.Id,
                    intent,
                    request.AgentProfile,
                    Truncate(phi.Redact(request.Message), 512),
                    JsonSerializer.Serialize(memories.Select(m => new { id = m.Record.Id, kind = m.Record.Kind, score = m.Score })),
                    JsonSerializer.Serialize(new { provider, model, promptTokens, completionTokens, costUsd, contextManifest, versionFingerprint }),
                    proposedAction,
                    evaluation.RiskLevel,
                    hadToolError ? 0.55 : 0.88,
                    evaluation.PolicyDecision,
                    evaluation.DecisionStatus,
                    evaluation.Reason,
                    request.CorrelationId), ct);
            }
            catch (Exception ex)
            {
                log.LogWarning(ex, "Failed to record autonomy decision for conversation {ConversationId}", conv.Id);
            }
        }

        // ── LLM06: screen output for accidental credential/secret leakage ─────
        var shieldResult = outputShield.Inspect(finalContent);
        if (shieldResult.HasLeak)
        {
            log.LogWarning("OutputShield redacted {Count} secret pattern(s) from agent response: {Types}",
                shieldResult.Detections.Count, string.Join(", ", shieldResult.Detections));
            finalContent = shieldResult.SafeContent;
        }

        // ── LLM01/LLM06 egress: strip spotlight tokens + redact PHI before the response
        //    leaves this process. Defence-in-depth layer over the output shield above.
        var egressCtx = new EgressContext(request.UserId, CallerSubject: null, AllowedPatientIds: []);
        var egressResult = egressGuard.Inspect(finalContent, egressCtx);
        if (!egressResult.Allowed)
        {
            log.LogWarning("egress.blocked reasons={Reasons} userId={UserId}",
                string.Join(",", egressResult.Reasons), request.UserId);
            HopeMeters.AgentRuns.Add(1, new KeyValuePair<string, object?>("outcome", "egress_blocked"));
        }
        finalContent = egressResult.SanitizedResponse;

        if (optional.ProvenanceStore is not null)
        {
            try
            {
                var sourceManifest = JsonSerializer.Serialize(new
                {
                    contextManifest,
                    sources = memories.Select(m => new
                    {
                        sourceId = m.Record.Id,
                        sourceType = m.Record.Kind.ToString(),
                        trustScore = Math.Round(m.Score, 4),
                        tokenBudget = _opts.MaxContextChars / 4,
                    }),
                    retrievalQuery = request.Message,
                    provider,
                    model,
                });
                var droppedContext = JsonSerializer.Serialize(new[]
                {
                    new
                    {
                        source = "assembled_context",
                        reason = messages.Sum(m => m.Content?.Length ?? 0) > _opts.MaxContextChars
                            ? "context_budget_exceeded"
                            : "none",
                        tokenBudget = _opts.MaxContextChars / 4,
                    },
                });
                await optional.ProvenanceStore.AddAsync(new ContextProvenanceWrite(
                    request.TenantId,
                    null,
                    conv.Id,
                    null,
                    null,
                    request.CorrelationId ?? activity?.TraceId.ToString() ?? conv.Id.ToString(),
                    Hash(finalContent),
                    request.Message,
                    sourceManifest,
                    droppedContext,
                    _opts.MaxContextChars / 4,
                    "treatment",
                    LooksSensitive(intent) ? DataSensitivity.Phi : DataSensitivity.Internal,
                    _opts.PolicyVersion), ct);
            }
            catch (Exception ex)
            {
                log.LogWarning(ex, "Failed to record context provenance for conversation {ConversationId}", conv.Id);
            }
        }

        return new AgentResponse(conv.Id, finalContent, toolExecutions, promptTokens, completionTokens, provider, model, sw.Elapsed, costUsd);
    }

    private static string Hash(string content)
        => Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(content))).ToLowerInvariant();

    private static bool LooksSensitive(string intent)
        => intent.Contains("medical", StringComparison.OrdinalIgnoreCase)
           || intent.Contains("clinical", StringComparison.OrdinalIgnoreCase)
           || intent.Contains("patient", StringComparison.OrdinalIgnoreCase)
           || intent.Contains("summary", StringComparison.OrdinalIgnoreCase)
           || intent.Contains("reminder", StringComparison.OrdinalIgnoreCase);

    private static string InferIntent(AgentRequest request)
    {
        if (!string.IsNullOrWhiteSpace(request.AgentProfile))
            return request.AgentProfile!;

        var text = request.Message.ToLowerInvariant();
        if (ContainsAny(text, "đặt lịch", "xếp lịch", "appointment", "booking", "reschedule", "tái khám"))
            return "scheduling";
        if (ContainsAny(text, "tóm tắt", "bệnh án", "soap", "summary", "pre-visit", "discharge"))
            return "medical_summary";
        if (ContainsAny(text, "nhắc thuốc", "nhắc tái khám", "reminder", "adherence", "uống thuốc"))
            return "reminder";
        if (ContainsAny(text, "audit", "kiểm toán", "tuân thủ", "compliance", "security report", "báo cáo"))
            return "audit_report";
        if (ContainsAny(text, "bảo hiểm", "insurance", "coverage", "claim"))
            return "insurance";
        if (ContainsAny(text, "cấp cứu", "đột quỵ", "stroke", "urgent", "triage", "sepsis", "nhồi máu"))
            return "emergency";
        if (ContainsAny(text, "guideline", "phác đồ", "chẩn đoán", "diagnosis", "drug interaction"))
            return "clinical";

        return "default";
    }

    private static HashSet<string> ResolveAllowedToolNames(string intent)
    {
        var normalized = intent.Replace("-", "_", StringComparison.OrdinalIgnoreCase).ToLowerInvariant();
        var names = normalized switch
        {
            "scheduling" or "schedule" or "appointment" =>
                new[]
                {
                    "map_specialty", "get_doctor_slots", "optimize_batch_appointments",
                    "commit_booking", "schedule_appointment", "verify_insurance",
                },
            "medical_summary" or "summary" or "soap_note" =>
                new[] { "patient_lookup", "search_clinical_guidelines", "persist_medical_summary" },
            "reminder" or "medication_reminder" =>
                new[] { "get_medication_schedule", "create_reminder_record", "update_reminder_status", "throttle_notifications" },
            "audit_report" or "audit" or "compliance_report" =>
                new[] { "collect_audit_logs", "detect_audit_anomalies", "export_audit_report" },
            "insurance" or "billing" =>
                new[] { "verify_insurance" },
            "clinical" or "emergency" =>
                new[] { "patient_lookup", "search_clinical_guidelines", "rank_triage_patients" },
            _ => Array.Empty<string>(),
        };

        return new HashSet<string>(names, StringComparer.OrdinalIgnoreCase);
    }

    private static bool ContainsAny(string text, params string[] terms)
        => terms.Any(term => text.Contains(term, StringComparison.OrdinalIgnoreCase));

    public async IAsyncEnumerable<string> StreamAsync(AgentRequest request, [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken ct)
    {
        var now = clock.UtcNow;
        var conv = await LoadOrCreateConversationAsync(request, now, ct);
        conv.AddMessage(MessageRole.User, request.Message, now);
        var memories = await RetrieveMemoriesAsync(request, ct);
        var messages = BuildMessages(conv, memories, Array.Empty<LearnedSkill>(), null, null);
        messages = EnforceContextBudget(messages, _opts.MaxContextChars);

        var chat = router.SelectChat();
        var sb = new System.Text.StringBuilder();
        string? previousChunk = null;
        await foreach (var chunk in chat.StreamAsync(new ChatRequest(messages, UserId: request.UserId.ToString()), ct))
        {
            if (string.Equals(chunk, previousChunk, StringComparison.Ordinal))
                continue;
            previousChunk = chunk;
            sb.Append(chunk);
            // ── M-07: per-chunk output shield on streaming path ──
            var safeChunk = chunk;
            var chunkShield = outputShield.Inspect(chunk);
            if (chunkShield.HasLeak)
            {
                safeChunk = chunkShield.SafeContent;
                log.LogWarning("OutputShield redacted streaming chunk: {Types}", string.Join(", ", chunkShield.Detections));
            }
            yield return safeChunk;
        }

        var fullContent = sb.ToString();

        // ── M-07 full-content egress check after streaming completes ──
        var egressCtx = new EgressContext(request.UserId, CallerSubject: null, AllowedPatientIds: []);
        var egressResult = egressGuard.Inspect(fullContent, egressCtx);
        if (!egressResult.Allowed)
        {
            log.LogWarning("egress.blocked on stream reasons={Reasons} userId={UserId}",
                string.Join(",", egressResult.Reasons), request.UserId);
        }
        var safeFull = egressResult.SanitizedResponse;
        conv.AddMessage(MessageRole.Assistant, safeFull, clock.UtcNow);
        await convRepo.SaveChangesAsync(ct);
    }

    private static List<ChatMessage> EnforceContextBudget(IReadOnlyList<ChatMessage> messages, int maxChars)
    {
        if (maxChars <= 0 || messages.Sum(m => m.Content?.Length ?? 0) <= maxChars)
            return messages.ToList();

        var remaining = maxChars;
        var kept = new List<ChatMessage>(messages.Count);
        foreach (var msg in messages)
        {
            var content = msg.Content ?? string.Empty;
            if (remaining <= 0)
            {
                if (msg.Role == "system")
                    kept.Add(msg with { Content = Truncate(content, Math.Min(content.Length, 1024)) });
                continue;
            }

            if (content.Length <= remaining)
            {
                kept.Add(msg);
                remaining -= content.Length;
            }
            else
            {
                kept.Add(msg with { Content = Truncate(content, remaining) });
                remaining = 0;
            }
        }

        return kept;
    }

    private async Task<Conversation> LoadOrCreateConversationAsync(AgentRequest request, DateTimeOffset now, CancellationToken ct)
    {
        if (request.ConversationId is { } id)
        {
            var existing = await convRepo.GetAsync(id, ct);
            if (existing is not null) return existing;
        }
        var conv = Conversation.Create(request.UserId, request.Message.Length > 60 ? request.Message[..60] : request.Message, now);
        await convRepo.AddAsync(conv, ct);
        return conv;
    }

    private async Task<IReadOnlyList<MemorySearchHit>> RetrieveMemoriesAsync(AgentRequest request, CancellationToken ct)
    {
        try
        {
            var embedder = router.SelectEmbedding();
            var embed = await embedder.EmbedAsync(new EmbeddingRequest([request.Message]), ct);

            // Fetch more candidates when reranking is enabled so the reranker has room to reorder.
            var fetchK = _opts.EnableMemoryReranking ? Math.Max(_opts.RerankCandidateK, _opts.MemoryTopK) : _opts.MemoryTopK;

            var hits = _opts.EnableHybridRetrieval
                ? await memory.SearchHybridAsync(request.UserId, embed.Vectors[0], request.Message, fetchK, kind: null, ct)
                : await memory.SearchAsync(request.UserId, embed.Vectors[0], fetchK, kind: null, ct);

            // ── NeMo Guardrails retrieval rail: drop chunks containing injection patterns ──
            hits = retrievalRail.Filter(hits);

            if (_opts.EnableMemoryReranking && optional.Reranker is not null && hits.Count > _opts.MemoryTopK)
            {
                hits = await optional.Reranker.RerankAsync(request.Message, hits, _opts.MemoryTopK, ct);
            }
            else if (hits.Count > _opts.MemoryTopK)
            {
                hits = hits.Take(_opts.MemoryTopK).ToList();
            }
            return hits;
        }
        catch (Exception ex)
        {
            log.LogWarning(ex, "Memory retrieval failed; proceeding without long-term context");
            return [];
        }
    }

    private List<ChatMessage> BuildMessages(
        Conversation conv,
        IReadOnlyList<MemorySearchHit> mems,
        IReadOnlyList<LearnedSkill> skills,
        UserTraitsSnapshot? traits = null,
        CompressionResult? compression = null,
        ClinicalContextFragment? clinicalFragment = null)
    {
        var list = new List<ChatMessage> { new("system", _opts.SystemPrompt) };
        // LLM01: spotlighting — instructs the model that delimited blocks are data, never instructions.
        list.Add(new ChatMessage("system", PromptSpotlight.SystemDirective));
        if (clinicalFragment is not null && !string.IsNullOrWhiteSpace(clinicalFragment.Content))
        {
            // Clinical context arrives from external HIS/EMR sources — treat as untrusted.
            list.Add(new ChatMessage("system",
                $"Clinical context for profile '{clinicalFragment.Profile}':\n{PromptSpotlight.Wrap(clinicalFragment.Content)}"));
        }
        if (traits is { IsEmpty: false })
        {
            list.Add(new ChatMessage("system", traits.ToSystemPromptFragment()));
        }
        if (compression is not null)
        {
            list.Add(new ChatMessage("system",
                $"Earlier-conversation summary (compressed {compression.CompressedMessageCount} older turns):\n{compression.Summary.Content}"));
        }
        if (mems.Count > 0)
        {
            // Memory hits are derived from user-supplied text; each chunk is spotlighted
            // so injected instructions embedded in stored memory cannot hijack the model.
            var memText = string.Join("\n", mems.Select((m, i) => $"[{i + 1}] ({m.Record.Kind}) {PromptSpotlight.Wrap(m.Record.Content)}"));
            list.Add(new ChatMessage("system", $"Relevant long-term memory:\n{memText}"));
        }
        if (skills.Count > 0)
        {
            // Skill answer templates are distilled from past runs and may embed user phrasing.
            var skillText = string.Join("\n", skills.Select((s, i) => $"[{i + 1}] {PromptSpotlight.Wrap(s.AnswerTemplate)} (reward={s.Reward:F2}, used={s.UsageCount})"));
            list.Add(new ChatMessage("system", $"Patterns that worked well before for similar requests:\n{skillText}"));
            HopeMeters.SkillHits.Add(skills.Count);
        }

        // ── I-03: context-budget enforcement — truncate memory/skill blocks if
        //    the total payload exceeds the configured char limit (rough proxy for tokens).
        ApplyContextBudget(list, _opts.MaxContextChars);

        IEnumerable<ConversationMessage> turns = conv.Messages.OrderBy(m => m.CreatedAt);
        if (compression is not null)
        {
            // Drop the older turns covered by the summary; keep only messages newer than the cutoff.
            turns = turns.Where(m => m.CreatedAt > compression.Summary.SummarizedUpTo);
        }
        foreach (var msg in turns)
        {
            var role = msg.Role switch
            {
                MessageRole.User => "user",
                MessageRole.Assistant => "assistant",
                MessageRole.Tool => "tool",
                _ => "system",
            };
            list.Add(new ChatMessage(role, msg.Content, msg.ToolName, msg.ToolCallId));
        }
        return list;
    }

    /// <summary>
    /// I-03: context-budget enforcement.  When total message payload exceeds <paramref name="maxChars"/>,
    /// truncates the largest system blocks (memories first, then skills) to keep the prompt within
    /// the model's context window.  History turns are preserved — only injected context is trimmed.
    /// </summary>
    private static void ApplyContextBudget(List<ChatMessage> messages, int maxChars)
    {
        var total = messages.Sum(m => m.Content?.Length ?? 0);
        if (total <= maxChars) return;

        var excess = total - maxChars;
        // Walk system messages in reverse (memories & skills are near the end of the system block)
        // and trim each until the excess is absorbed.
        for (int i = messages.Count - 1; i >= 0 && excess > 0; i--)
        {
            if (messages[i].Role != "system") continue;
            var content = messages[i].Content;
            if (string.IsNullOrWhiteSpace(content)) continue;

            var keep = Math.Max(0, content.Length - excess);
            if (keep <= 0)
            {
                excess -= content.Length;
                messages[i] = messages[i] with { Content = "[truncated]" };
            }
            else
            {
                messages[i] = messages[i] with { Content = content[..keep] + "\n[truncated]" };
                excess = 0;
            }
        }
    }

    /// <summary>
    /// Thread-safe core of tool execution used by the parallel fan-out path (C-5).
    /// Performs all validation, RBAC, approval, sandbox invocation, and security
    /// screening WITHOUT modifying the conversation. The caller is responsible for
    /// appending conversation messages after all parallel results are collected.
    /// </summary>
    private async Task<(string output, AgentToolExecution exec)> ExecuteToolCoreAsync(ToolCall call, AgentRequest request, CancellationToken ct)
    {
        using var span = Activity.StartActivity($"tool.{call.Name}");
        var sw = Stopwatch.StartNew();

        // ── Per-tool circuit breaker ──
        if (_toolFailureCount.TryGetValue(call.Name, out var toolFails) && toolFails >= _opts.MaxConsecutiveToolFailures)
        {
            var circuitErr = JsonSerializer.Serialize(new { error = "tool_circuit_open", tool = call.Name, consecutive_failures = toolFails });
            log.LogWarning("Tool {Tool} circuit open after {Fails} consecutive failures", call.Name, toolFails);
            return (circuitErr, new AgentToolExecution(call.Name, call.ArgumentsJson, circuitErr, sw.Elapsed, false));
        }

        var tool = tools.Find(call.Name);
        if (tool is null)
        {
            var err = JsonSerializer.Serialize(new { error = "tool_not_found", tool = call.Name });
            return (err, new AgentToolExecution(call.Name, call.ArgumentsJson, err, sw.Elapsed, false));
        }

        // ── H-7: distributed lock — prevent duplicate execution across instances ──
        var argsHash = Convert.ToHexString(
            System.Security.Cryptography.SHA256.HashData(
                System.Text.Encoding.UTF8.GetBytes(call.ArgumentsJson))).ToLowerInvariant();
        ILockHandle? toolLock = null;
        if (optional.DistributedLock is not null)
        {
            var lockKey = $"tool:{call.Name}:{request.UserId.ToString("N")}:{argsHash}";
            toolLock = await optional.DistributedLock.AcquireAsync(lockKey, _opts.ToolExecutionTimeout, ct);
            if (toolLock is null)
            {
                log.LogDebug("Tool {Tool} skipped — another instance is executing it", call.Name);
                var dupErr = JsonSerializer.Serialize(new { error = "tool_duplicate", tool = call.Name, reason = "another_instance_executing" });
                return (dupErr, new AgentToolExecution(call.Name, call.ArgumentsJson, dupErr, sw.Elapsed, true));
            }
            log.LogDebug("Distributed lock acquired for tool {Tool}", call.Name);
        }

        try
        {
            var ctx = new ToolInvocationContext(request.UserId, Guid.Empty, request.CorrelationId ?? string.Empty, request.Roles);

            var autonomyDenied = await EvaluateAutonomyForToolAsync(call, request, Guid.Empty, ct);
            if (autonomyDenied is not null)
                return (autonomyDenied, new AgentToolExecution(call.Name, call.ArgumentsJson, autonomyDenied, sw.Elapsed, false));

            // ── LLM08: RBAC ──
            if (!accessPolicy.IsAllowed(call.Name, request.Roles ?? []))
            {
                var deny = JsonSerializer.Serialize(new { error = "tool_access_denied", tool = call.Name, reason = "insufficient_role" });
                HopeMeters.ToolApprovalsDenied.Add(1,
                    new("tool", call.Name), new("reason", "rbac"));
                return (deny, new AgentToolExecution(call.Name, call.ArgumentsJson, deny, sw.Elapsed, false));
            }

            var policy = approvalPolicy.Decide(call.Name, call.ArgumentsJson);
            if (policy.Kind == ApprovalDecisionKind.AutoDeny)
            {
                var deny = JsonSerializer.Serialize(new { error = "tool_execution_denied", reason = policy.Reason ?? "policy_auto_deny", tool = call.Name });
                HopeMeters.ToolApprovalsDenied.Add(1, new("tool", call.Name), new("reason", "policy"));
                return (deny, new AgentToolExecution(call.Name, call.ArgumentsJson, deny, sw.Elapsed, false));
            }
            if (policy.Kind == ApprovalDecisionKind.RequireApproval)
            {
                // Approval gate requires conversation context — use a temp conv ID
                var approval = await approvalGate.RequestAsync(
                    new ApprovalRequestInput(Guid.Empty, request.UserId, call.Name, call.ArgumentsJson, policy.Impact), ct);
                if (!approval.Approved)
                {
                    var deny = JsonSerializer.Serialize(new { error = "tool_execution_denied", reason = approval.Reason ?? "not_approved", status = approval.Status.ToString(), tool = call.Name });
                    return (deny, new AgentToolExecution(call.Name, call.ArgumentsJson, deny, sw.Elapsed, false));
                }
            }

            // ── M-04: tool execution timeout ──
            using var toolCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
            toolCts.CancelAfter(_opts.ToolExecutionTimeout);

            var output = (await sandbox.InvokeAsync(tool, call.ArgumentsJson, ctx, toolCts.Token)) ?? "{}";

            // ── I-05: screen for injection patterns ──
            if (ContainsInjectionPattern(output))
            {
                log.LogWarning("Injection pattern detected in output of tool {Tool}", call.Name);
                output = JsonSerializer.Serialize(new { error = "tool_output_flagged", reason = "injection_pattern_detected", tool = call.Name });
                _toolFailureCount[call.Name] = _toolFailureCount.GetValueOrDefault(call.Name) + 1;
                return (output, new AgentToolExecution(call.Name, call.ArgumentsJson, output, sw.Elapsed, false));
            }

            _toolFailureCount.Remove(call.Name); // reset on success
            return (output, new AgentToolExecution(call.Name, call.ArgumentsJson, output, sw.Elapsed, true));
        }
        catch (Exception ex)
        {
            log.LogError(ex, "Tool {Tool} failed", call.Name);
            HopeMeters.ToolErrors.Add(1, new KeyValuePair<string, object?>("tool", call.Name));
            _toolFailureCount[call.Name] = _toolFailureCount.GetValueOrDefault(call.Name) + 1;
            var err = JsonSerializer.Serialize(new { error = "tool_failure", message = ex.Message });
            return (err, new AgentToolExecution(call.Name, call.ArgumentsJson, err, sw.Elapsed, false));
        }
        finally
        {
            if (toolLock is not null)
                await ((IAsyncDisposable)toolLock).DisposeAsync();
        }
    }

    private async Task<(string output, AgentToolExecution exec)> ExecuteToolAsync(ToolCall call, AgentRequest request, Conversation conv, CancellationToken ct)
    {
        using var span = Activity.StartActivity($"tool.{call.Name}");
        var sw = Stopwatch.StartNew();

        // ── Per-tool circuit breaker: skip tools that have failed too many times ──
        if (_toolFailureCount.TryGetValue(call.Name, out var toolFails) && toolFails >= _opts.MaxConsecutiveToolFailures)
        {
            var circuitErr = JsonSerializer.Serialize(new { error = "tool_circuit_open", tool = call.Name, consecutive_failures = toolFails });
            conv.AddMessage(MessageRole.Tool, circuitErr, clock.UtcNow, call.Name, call.Id);
            log.LogWarning("Tool {Tool} circuit open after {Fails} consecutive failures", call.Name, toolFails);
            return (circuitErr, new AgentToolExecution(call.Name, call.ArgumentsJson, circuitErr, sw.Elapsed, false));
        }

        var tool = tools.Find(call.Name);
        if (tool is null)
        {
            var err = JsonSerializer.Serialize(new { error = "tool_not_found", tool = call.Name });
            conv.AddMessage(MessageRole.Tool, err, clock.UtcNow, call.Name, call.Id);
            return (err, new AgentToolExecution(call.Name, call.ArgumentsJson, err, sw.Elapsed, false));
        }
        try
        {
            var ctx = new ToolInvocationContext(request.UserId, conv.Id, request.CorrelationId ?? conv.Id.ToString(), request.Roles);

            var autonomyDenied = await EvaluateAutonomyForToolAsync(call, request, conv.Id, ct);
            if (autonomyDenied is not null)
            {
                conv.AddMessage(MessageRole.Tool, autonomyDenied, clock.UtcNow, call.Name, call.Id);
                return (autonomyDenied, new AgentToolExecution(call.Name, call.ArgumentsJson, autonomyDenied, sw.Elapsed, false));
            }

            // ── LLM08: RBAC — check role-based tool access before approval ───
            if (!accessPolicy.IsAllowed(call.Name, request.Roles ?? []))
            {
                var deny = JsonSerializer.Serialize(new { error = "tool_access_denied", tool = call.Name, reason = "insufficient_role" });
                conv.AddMessage(MessageRole.Tool, deny, clock.UtcNow, call.Name, call.Id);
                log.LogWarning("Tool {Tool} denied for user {User}: insufficient role. Roles=[{Roles}]",
                    call.Name, request.UserId, string.Join(",", request.Roles ?? []));
                HopeMeters.ToolApprovalsDenied.Add(1,
                    new KeyValuePair<string, object?>("tool", call.Name),
                    new KeyValuePair<string, object?>("reason", "rbac"));
                return (deny, new AgentToolExecution(call.Name, call.ArgumentsJson, deny, sw.Elapsed, false));
            }

            var policy = approvalPolicy.Decide(call.Name, call.ArgumentsJson);
            if (policy.Kind == ApprovalDecisionKind.AutoDeny)
            {
                var deny = JsonSerializer.Serialize(new { error = "tool_execution_denied", reason = policy.Reason ?? "policy_auto_deny", tool = call.Name });
                conv.AddMessage(MessageRole.Tool, deny, clock.UtcNow, call.Name, call.Id);
                HopeMeters.ToolApprovalsDenied.Add(1, new KeyValuePair<string, object?>("tool", call.Name), new KeyValuePair<string, object?>("reason", "policy"));
                return (deny, new AgentToolExecution(call.Name, call.ArgumentsJson, deny, sw.Elapsed, false));
            }
            if (policy.Kind == ApprovalDecisionKind.RequireApproval)
            {
                var approval = await approvalGate.RequestAsync(
                    new ApprovalRequestInput(conv.Id, request.UserId, call.Name, call.ArgumentsJson, policy.Impact),
                    ct);
                if (!approval.Approved)
                {
                    var deny = JsonSerializer.Serialize(new { error = "tool_execution_denied", reason = approval.Reason ?? "not_approved", status = approval.Status.ToString(), tool = call.Name });
                    conv.AddMessage(MessageRole.Tool, deny, clock.UtcNow, call.Name, call.Id);
                    return (deny, new AgentToolExecution(call.Name, call.ArgumentsJson, deny, sw.Elapsed, false));
                }
            }

            // ── M-04: tool execution timeout — prevents hanging on slow HIS APIs ──
            using var toolCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
            toolCts.CancelAfter(_opts.ToolExecutionTimeout);

            var output = (await sandbox.InvokeAsync(tool, call.ArgumentsJson, ctx, toolCts.Token)) ?? "{}"; // ── H-02: null-coalesce

            // ── I-05: screen tool output for indirect prompt-injection patterns ──
            if (ContainsInjectionPattern(output))
            {
                log.LogWarning("Injection pattern detected in output of tool {Tool}", call.Name);
                output = JsonSerializer.Serialize(new { error = "tool_output_flagged", reason = "injection_pattern_detected", tool = call.Name });
                conv.AddMessage(MessageRole.Tool, output, clock.UtcNow, call.Name, call.Id);
                _toolFailureCount[call.Name] = _toolFailureCount.GetValueOrDefault(call.Name) + 1;
                return (output, new AgentToolExecution(call.Name, call.ArgumentsJson, output, sw.Elapsed, false));
            }

            conv.AddMessage(MessageRole.Tool, output, clock.UtcNow, call.Name, call.Id);
            _toolFailureCount.Remove(call.Name); // reset on success
            return (output, new AgentToolExecution(call.Name, call.ArgumentsJson, output, sw.Elapsed, true));
        }
        catch (Exception ex)
        {
            log.LogError(ex, "Tool {Tool} failed", call.Name);
            HopeMeters.ToolErrors.Add(1, new KeyValuePair<string, object?>("tool", call.Name));
            _toolFailureCount[call.Name] = _toolFailureCount.GetValueOrDefault(call.Name) + 1;
            var err = JsonSerializer.Serialize(new { error = "tool_failure", message = ex.Message });
            conv.AddMessage(MessageRole.Tool, err, clock.UtcNow, call.Name, call.Id);
            return (err, new AgentToolExecution(call.Name, call.ArgumentsJson, err, sw.Elapsed, false));
        }
    }

    private async Task<string?> EvaluateAutonomyForToolAsync(ToolCall call, AgentRequest request, Guid conversationId, CancellationToken ct)
    {
        if (optional.Autonomy is null) return null;

        try
        {
            var intent = InferIntent(request);
            var evaluation = optional.Autonomy.Evaluate(new AutonomyEvaluationRequest(
                request.UserId,
                null,
                conversationId == Guid.Empty ? null : conversationId,
                intent,
                request.AgentProfile,
                request.Message,
                call.Name,
                call.ArgumentsJson,
                0.9,
                request.CorrelationId));

            await optional.Autonomy.RecordDecisionAsync(new AgentDecisionWrite(
                request.UserId,
                null,
                conversationId == Guid.Empty ? null : conversationId,
                intent,
                request.AgentProfile,
                Truncate(phi.Redact(request.Message), 512),
                null,
                JsonSerializer.Serialize(new { tool = call.Name }),
                JsonSerializer.Serialize(new { tool = call.Name, arguments = TryParseJson(call.ArgumentsJson) }),
                evaluation.RiskLevel,
                0.9,
                evaluation.PolicyDecision,
                evaluation.DecisionStatus,
                evaluation.Reason,
                request.CorrelationId), ct);

            if (evaluation.PolicyDecision == AutonomyPolicyDecision.AutoDeny)
            {
                return JsonSerializer.Serialize(new
                {
                    error = "autonomy_denied",
                    tool = call.Name,
                    reason = evaluation.Reason,
                    risk = evaluation.RiskLevel.ToString(),
                });
            }

            if (evaluation.PolicyDecision == AutonomyPolicyDecision.RequireApproval)
            {
                var approval = await approvalGate.RequestAsync(
                    new ApprovalRequestInput(
                        conversationId,
                        request.UserId,
                        call.Name,
                        call.ArgumentsJson,
                        evaluation.RiskLevel == AutonomyRiskLevel.Critical ? ToolImpactLevel.Critical : ToolImpactLevel.Write),
                    ct);
                if (!approval.Approved)
                {
                    return JsonSerializer.Serialize(new
                    {
                        error = "autonomy_approval_denied",
                        tool = call.Name,
                        reason = approval.Reason ?? evaluation.Reason,
                        status = approval.Status.ToString(),
                    });
                }
            }
        }
        catch (Exception ex)
        {
            log.LogWarning(ex, "Autonomy policy check failed for tool {Tool}; continuing with existing tool approval policy", call.Name);
        }

        return null;
    }

    private static object TryParseJson(string json)
    {
        try { return JsonSerializer.Deserialize<JsonElement>(json); }
        catch { return json; }
    }

    /// <summary>
    /// Appends the assistant response to the message list, including a JSON-serialised
    /// copy of the tool_calls so providers can reconstruct the conversation state (M-06).
    /// </summary>
    private static List<ChatMessage> AppendAssistantToolCalls(List<ChatMessage> messages, ChatResponse resp)
    {
        var tcJson = resp.ToolCalls.Count > 0
            ? JsonSerializer.Serialize(resp.ToolCalls.Select(tc => new { id = tc.Id, type = "function", function = new { name = tc.Name, arguments = tc.ArgumentsJson } }))
            : null;
        var copy = new List<ChatMessage>(messages.Count + 1);
        copy.AddRange(messages);
        copy.Add(new ChatMessage("assistant", resp.Content, ToolCallsJson: tcJson));
        return copy;
    }

    private static List<ChatMessage> AppendToolResult(List<ChatMessage> messages, ToolCall call, string output)
    {
        // ── M-03: use AddRange instead of full-copy constructor to reduce pressure.
        var copy = new List<ChatMessage>(messages.Count + 1);
        copy.AddRange(messages);
        copy.Add(new ChatMessage("tool", output, call.Name, call.Id));
        return copy;
    }

    /// <summary>
    /// Background persistence target. Replaces the old sync PersistMemoryAsync (H-03).
    /// </summary>
    private async Task StoreEpisodicFallbackAsync(Guid userId, Guid convId, string userMessage, string finalContent, string? agentProfile, CancellationToken ct)
    {
        try
        {
            var summary = $"User asked: {userMessage}\nAssistant replied: {finalContent}";
            var embedder = router.SelectEmbedding();
            var vec = (await embedder.EmbedAsync(new EmbeddingRequest([summary]), ct)).Vectors[0];

            // Dedup: if a nearly identical memory already exists, just boost its importance
            // rather than inserting a duplicate. Threshold 0.92 ≈ same topic + same answer.
            var similar = await memory.FindSimilarAsync(userId, vec, 0.92f, ct);
            if (similar.Count > 0)
            {
                await memory.BumpImportanceAsync(similar[0].Record.Id, 0.05f, ct);
                return;
            }

            var metadata = string.IsNullOrWhiteSpace(agentProfile)
                ? new Dictionary<string, string>()
                : new Dictionary<string, string> { ["agent_profile"] = agentProfile };
            await memory.UpsertAsync(new MemoryRecord
            {
                Id = Guid.CreateVersion7(),
                UserId = userId,
                ConversationId = convId,
                Kind = MemoryKind.Episodic,
                Content = summary,
                Source = "agent_runtime",
                Importance = 0.5f,
                Metadata = metadata,
                CreatedAt = clock.UtcNow,
            }, vec, ct);
        }
        catch (Exception ex)
        {
            log.LogWarning(ex, "Failed to persist episodic memory");
        }
    }

    private async Task<IReadOnlyList<LearnedSkill>> SafeRetrieveSkillsAsync(string intent, CancellationToken ct)
    {
        try { return await skillLibrary.RetrieveByIntentAsync(intent, _opts.SkillTopK, ct); }
        catch (Exception ex)
        {
            log.LogWarning(ex, "Skill retrieval failed");
            return Array.Empty<LearnedSkill>();
        }
    }

    private async Task DistillSkillAsync(string intent, string userMessage, string finalContent, IReadOnlyList<AgentToolExecution> execs, CancellationToken ct)
    {
        try
        {
            var signature = Convert.ToHexString(
                System.Security.Cryptography.SHA256.HashData(
                    System.Text.Encoding.UTF8.GetBytes(intent + "|" + Truncate(userMessage, 256))))[..32];

            var skill = new LearnedSkill
            {
                Id = Guid.CreateVersion7(),
                Intent = intent,
                Signature = signature,
                ToolSequenceJson = JsonSerializer.Serialize(execs.Select(e => new { e.Tool, e.Success }).ToArray()),
                AnswerTemplate = Truncate(finalContent, 512),
                Reward = 0.5,
                UsageCount = 1,
                CreatedAt = clock.UtcNow,
                LastUsed = clock.UtcNow,
            };
            await skillLibrary.RecordSuccessAsync(skill, ct);
        }
        catch (Exception ex)
        {
            log.LogDebug(ex, "Skill distillation skipped");
        }
    }

    private static string Truncate(string s, int max) => s.Length <= max ? s : s[..max];

    // ── M-02: safe async wrappers for parallel context gathering ────────────

    private async Task<UserTraitsSnapshot?> SafeGetUserTraitsAsync(Guid userId, CancellationToken ct)
    {
        try { return await userModel.GetAsync(userId, ct); }
        catch (Exception ex) { log.LogWarning(ex, "User-model fetch failed; ignoring"); return null; }
    }

    private async Task<CompressionResult?> SafeCompressAsync(Conversation conv, CancellationToken ct)
    {
        try { return await compressor.MaybeCompressAsync(conv, ct); }
        catch (Exception ex) { log.LogWarning(ex, "Conversation compression failed; ignoring"); return null; }
    }

    private static async Task<ClinicalContextFragment?> SafeGetClinicalContextAsync(
        IClinicalContextProvider provider, string? profile, CancellationToken ct)
    {
        try { return await provider.GetAsync(profile, ct); }
        catch (Exception) { return null; }
    }

    // ── I-05: lightweight injection-pattern detector for tool output ────────

    private static bool ContainsInjectionPattern(string text)
    {
        if (string.IsNullOrWhiteSpace(text)) return false;
        var lowered = text.ToLowerInvariant();
        // Broad-spectrum patterns: prompt-leak / override / ignore-instruction markers
        return lowered.Contains("ignore previous instructions", StringComparison.Ordinal)
            || lowered.Contains("ignore all instructions", StringComparison.Ordinal)
            || lowered.Contains("system prompt:", StringComparison.Ordinal)
            || lowered.Contains("<<<instruction>>>", StringComparison.Ordinal)
            || lowered.Contains("begin roleplay:", StringComparison.Ordinal)
            || (lowered.Contains("you are now", StringComparison.Ordinal) && lowered.Contains("assistant", StringComparison.Ordinal));
    }

    private async Task IngestKnowledgeAsync(string userMessage, string assistant, CancellationToken ct)
    {
        try
        {
            var text = $"User: {Truncate(userMessage, 1500)}\nAssistant: {Truncate(assistant, 1500)}";
            var extracted = await kgExtractor.ExtractAsync(text, ct);
            if (extracted.Entities.Count == 0 && extracted.Relations.Count == 0) return;
            await kgStore.UpsertAsync(extracted, ct);
            HopeMeters.KgEntitiesIngested.Add(extracted.Entities.Count);
            HopeMeters.KgRelationsIngested.Add(extracted.Relations.Count);
        }
        catch (Exception ex)
        {
            log.LogDebug(ex, "KG ingestion skipped");
        }
    }

    private async Task RunShadowAsync(
        string intent,
        IChatCompletionProvider champion,
        AgentRequest request,
        List<ChatMessage> messages,
        string championAnswer,
        CancellationToken ct)
    {
        try
        {
            var cfg = await shadow.GetActiveChallengerAsync(intent, ct);
            if (cfg is null || cfg.ChallengerProvider.Equals(champion.Name, StringComparison.OrdinalIgnoreCase)) return;
            if (Random.Shared.NextDouble() >= cfg.TrafficFraction) return;
            if (!_chatByName.TryGetValue(cfg.ChallengerProvider, out var chal)) return;

            var swc = Stopwatch.StartNew();
            var chalResp = await chal.CompleteAsync(new ChatRequest(messages, Temperature: 0.2f), ct);
            swc.Stop();
            var chalAnswer = chalResp.Content ?? string.Empty;

            var championScore = await judge.ScoreAsync(request.Message, championAnswer, null, ct);
            var challengerScore = await judge.ScoreAsync(request.Message, chalAnswer, null, ct);

            var won = challengerScore.Score > championScore.Score;
            await shadow.RecordAsync(new ShadowComparison
            {
                Id = Guid.CreateVersion7(),
                Intent = intent,
                ChampionProvider = champion.Name,
                ChallengerProvider = chal.Name,
                ChampionScore = championScore.Score,
                ChallengerScore = challengerScore.Score,
                ChallengerWon = won,
                LatencyDeltaMs = swc.ElapsedMilliseconds,
                CreatedAt = clock.UtcNow,
            }, ct);
        }
        catch (Exception ex)
        {
            log.LogDebug(ex, "Shadow A/B skipped");
        }
    }
}
