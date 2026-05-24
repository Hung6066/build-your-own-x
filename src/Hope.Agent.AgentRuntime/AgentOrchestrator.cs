using System.Diagnostics;
using System.Text.Json;
using Hope.Agent.Application.Abstractions;
using Hope.Agent.Application.Agents;
using Hope.Agent.Application.Compression;
using Hope.Agent.Application.Context;
using Hope.Agent.Application.Knowledge;
using Hope.Agent.Application.LLM;
using Hope.Agent.Application.Learning;
using Hope.Agent.Application.Observability;
using Hope.Agent.Application.Security;
using Hope.Agent.Application.Tools;
using Hope.Agent.Application.UserModeling;
using Hope.Agent.Domain.Audit;
using Hope.Agent.Domain.Conversations;
using Hope.Agent.Domain.Learning;
using Hope.Agent.Domain.Memory;
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
    Hope.Agent.AgentRuntime.Security.SandboxedToolExecutor sandbox,
    IConversationRepository convRepo,
    IMemoryStore memory,
    IConversationCompressor compressor,
    IUserModelService userModel,
    IAuditSink audit,
    IPromptShield shield,
    IPhiRedactor phi,
    IClock clock,
    IOptions<AgentRuntimeOptions> opts,
    ILogger<AgentOrchestrator> log,
    IClinicalContextProvider? clinicalContext = null) : IAgentRuntime
{
    private static readonly ActivitySource Activity = new("Hope.Agent.Runtime");
    private readonly AgentRuntimeOptions _opts = opts.Value;
    private readonly Dictionary<string, IChatCompletionProvider> _chatByName =
        chatProviders.ToDictionary(p => p.Name, StringComparer.OrdinalIgnoreCase);

    public async Task<AgentResponse> RunAsync(AgentRequest request, CancellationToken ct)
    {
        using var activity = Activity.StartActivity("agent.run");
        activity?.SetTag("user.id", request.UserId);
        var sw = Stopwatch.StartNew();
        var now = clock.UtcNow;

        var inspection = shield.Inspect(request.Message);
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

        var intent = string.IsNullOrWhiteSpace(request.AgentProfile) ? "default" : request.AgentProfile!;
        var memories = await RetrieveMemoriesAsync(request, ct);
        var skillHits = _opts.EnableSkillRetrieval
            ? await SafeRetrieveSkillsAsync(intent, ct)
            : (IReadOnlyList<LearnedSkill>)Array.Empty<LearnedSkill>();
        UserTraitsSnapshot? traits = null;
        try { traits = await userModel.GetAsync(request.UserId, ct); }
        catch (Exception ex) { log.LogWarning(ex, "User-model fetch failed; ignoring"); }
        CompressionResult? compression = null;
        try { compression = await compressor.MaybeCompressAsync(conv, ct); }
        catch (Exception ex) { log.LogWarning(ex, "Conversation compression failed; ignoring"); }
        ClinicalContextFragment? clinicalFragment = null;
        if (clinicalContext is not null)
        {
            try { clinicalFragment = await clinicalContext.GetAsync(request.AgentProfile, ct); }
            catch (Exception ex) { log.LogWarning(ex, "Clinical context load failed; ignoring"); }
        }
        var messages = BuildMessages(conv, memories, skillHits, traits, compression, clinicalFragment);

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
        var toolDefs = tools.All.Select(t => t.Definition).ToList();
        var toolExecutions = new List<AgentToolExecution>();

        int promptTokens = 0, completionTokens = 0;
        string provider = chat.Name, model = string.Empty;
        string finalContent = string.Empty;

        for (int iter = 0; iter < _opts.MaxToolIterations; iter++)
        {
            var resp = await chat.CompleteAsync(new ChatRequest(messages, Tools: toolDefs, UserId: request.UserId.ToString()), ct);
            provider = resp.Provider;
            model = resp.Model;
            promptTokens += resp.Usage.PromptTokens;
            completionTokens += resp.Usage.CompletionTokens;

            if (resp.ToolCalls.Count == 0)
            {
                finalContent = resp.Content;
                conv.AddMessage(MessageRole.Assistant, finalContent, clock.UtcNow);
                break;
            }

            messages = AppendAssistantToolCalls(messages, resp);
            foreach (var call in resp.ToolCalls)
            {
                var (output, exec) = await ExecuteToolAsync(call, request, conv, ct);
                toolExecutions.Add(exec);
                messages = AppendToolResult(messages, call, output);
            }
        }

        await convRepo.SaveChangesAsync(ct);
        await StoreEpisodicAsync(request, conv, finalContent, ct);

        var convIdForExtract = conv.Id;
        var userIdForExtract = request.UserId;
        _ = Task.Run(async () =>
        {
            try { await userModel.TryExtractAsync(userIdForExtract, convIdForExtract, CancellationToken.None); }
            catch (Exception ex) { log.LogWarning(ex, "User-model extract (background) failed"); }
        }, CancellationToken.None);

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
            _ = DistillSkillAsync(intent, request.Message, finalContent, toolExecutions, CancellationToken.None);
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
        HopeMeters.AgentRuns.Add(1, new KeyValuePair<string, object?>("outcome", "ok"));
        HopeMeters.AgentRunDurationMs.Record(sw.Elapsed.TotalMilliseconds, providerTag, modelTag);

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
            PayloadJson = JsonSerializer.Serialize(new { tools = toolExecutions.Select(t => t.Tool), provider, model, promptTokens, completionTokens, redactedInput = phi.Redact(request.Message) }),
        }, ct);

        return new AgentResponse(conv.Id, finalContent, toolExecutions, promptTokens, completionTokens, provider, model, sw.Elapsed);
    }

    public async IAsyncEnumerable<string> StreamAsync(AgentRequest request, [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken ct)
    {
        var now = clock.UtcNow;
        var conv = await LoadOrCreateConversationAsync(request, now, ct);
        conv.AddMessage(MessageRole.User, request.Message, now);
        var memories = await RetrieveMemoriesAsync(request, ct);
        var messages = BuildMessages(conv, memories, Array.Empty<LearnedSkill>(), null, null);

        var chat = router.SelectChat();
        var sb = new System.Text.StringBuilder();
        await foreach (var chunk in chat.StreamAsync(new ChatRequest(messages, UserId: request.UserId.ToString()), ct))
        {
            sb.Append(chunk);
            yield return chunk;
        }
        conv.AddMessage(MessageRole.Assistant, sb.ToString(), clock.UtcNow);
        await convRepo.SaveChangesAsync(ct);
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
            return await memory.SearchAsync(request.UserId, embed.Vectors[0], _opts.MemoryTopK, kind: null, ct);
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
        if (clinicalFragment is not null && !string.IsNullOrWhiteSpace(clinicalFragment.Content))
        {
            list.Add(new ChatMessage("system",
                $"Clinical context for profile '{clinicalFragment.Profile}':\n{clinicalFragment.Content}"));
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
            var memText = string.Join("\n", mems.Select((m, i) => $"[{i + 1}] ({m.Record.Kind}) {m.Record.Content}"));
            list.Add(new ChatMessage("system", $"Relevant long-term memory:\n{memText}"));
        }
        if (skills.Count > 0)
        {
            var skillText = string.Join("\n", skills.Select((s, i) => $"[{i + 1}] {s.AnswerTemplate} (reward={s.Reward:F2}, used={s.UsageCount})"));
            list.Add(new ChatMessage("system", $"Patterns that worked well before for similar requests:\n{skillText}"));
            HopeMeters.SkillHits.Add(skills.Count);
        }
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

    private async Task<(string output, AgentToolExecution exec)> ExecuteToolAsync(ToolCall call, AgentRequest request, Conversation conv, CancellationToken ct)
    {
        using var span = Activity.StartActivity($"tool.{call.Name}");
        var sw = Stopwatch.StartNew();
        var tool = tools.Find(call.Name);
        if (tool is null)
        {
            var err = JsonSerializer.Serialize(new { error = "tool_not_found", tool = call.Name });
            conv.AddMessage(MessageRole.Tool, err, clock.UtcNow, call.Name, call.Id);
            return (err, new AgentToolExecution(call.Name, call.ArgumentsJson, err, sw.Elapsed, false));
        }
        try
        {
            var ctx = new ToolInvocationContext(request.UserId, conv.Id, request.CorrelationId ?? conv.Id.ToString());

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

            var output = await sandbox.InvokeAsync(tool, call.ArgumentsJson, ctx, ct);
            conv.AddMessage(MessageRole.Tool, output, clock.UtcNow, call.Name, call.Id);
            return (output, new AgentToolExecution(call.Name, call.ArgumentsJson, output, sw.Elapsed, true));
        }
        catch (Exception ex)
        {
            log.LogError(ex, "Tool {Tool} failed", call.Name);
            HopeMeters.ToolErrors.Add(1, new KeyValuePair<string, object?>("tool", call.Name));
            var err = JsonSerializer.Serialize(new { error = "tool_failure", message = ex.Message });
            conv.AddMessage(MessageRole.Tool, err, clock.UtcNow, call.Name, call.Id);
            return (err, new AgentToolExecution(call.Name, call.ArgumentsJson, err, sw.Elapsed, false));
        }
    }

    private static List<ChatMessage> AppendAssistantToolCalls(List<ChatMessage> messages, ChatResponse resp)
    {
        var copy = new List<ChatMessage>(messages)
        {
            new("assistant", resp.Content),
        };
        return copy;
    }

    private static List<ChatMessage> AppendToolResult(List<ChatMessage> messages, ToolCall call, string output)
    {
        var copy = new List<ChatMessage>(messages)
        {
            new("tool", output, call.Name, call.Id),
        };
        return copy;
    }

    private async Task StoreEpisodicAsync(AgentRequest request, Conversation conv, string finalContent, CancellationToken ct)
    {
        try
        {
            var summary = $"User asked: {request.Message}\nAssistant replied: {finalContent}";
            var embedder = router.SelectEmbedding();
            var vec = (await embedder.EmbedAsync(new EmbeddingRequest([summary]), ct)).Vectors[0];
            await memory.UpsertAsync(new MemoryRecord
            {
                Id = Guid.CreateVersion7(),
                UserId = request.UserId,
                ConversationId = conv.Id,
                Kind = MemoryKind.Episodic,
                Content = summary,
                Source = "agent_runtime",
                Importance = 0.5f,
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