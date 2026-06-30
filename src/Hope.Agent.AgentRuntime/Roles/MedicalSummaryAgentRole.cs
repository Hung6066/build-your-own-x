using Hope.Agent.Application.Agents.Multi;
using Hope.Agent.Application.Rag;
using Hope.Agent.Application.Security;
using Hope.Agent.Application.LLM;
using Hope.Agent.Application.Tools;
using Microsoft.Extensions.Logging;
using System.Text.Json;

namespace Hope.Agent.AgentRuntime.Roles;

/// <summary>
/// Medical Summary Agent — generates context-aware pre-visit summaries, SOAP notes,
/// and patient-friendly summaries from EHR data pulled via RAG / MCP tools.
/// Reference: Abridge Contextual Reasoning Engine, Epic pre-visit AI, Microsoft Dragon Copilot.
/// </summary>
internal sealed class MedicalSummaryAgentRole : IAgentRole
{
    private readonly IRetrievalRail retrievalRail;
    private readonly IOutputShield outputShield;
    private readonly IPromptShield promptShield;
    private readonly IToolRegistry tools;
    private readonly ILLMRouter llm;
    private readonly ILogger<MedicalSummaryAgentRole> log;

    public MedicalSummaryAgentRole(
        IRetrievalRail retrievalRail,
        IOutputShield outputShield,
        IPromptShield promptShield,
        IToolRegistry tools,
        ILLMRouter llm,
        ILogger<MedicalSummaryAgentRole> log)
    {
        this.retrievalRail = retrievalRail;
        this.outputShield = outputShield;
        this.promptShield = promptShield;
        this.tools = tools;
        this.llm = llm;
        this.log = log;
    }

    public string Name => "medical-summary";
    public string Description => "Generates SOAP notes, pre-visit summaries and patient-friendly records from EHR context.";
    public IReadOnlyList<string> Intents =>
    [
        "summarize_record", "tom_tat_benh_an", "pre_visit_summary",
        "soap_note", "medical_summary", "benh_an", "lich_su_benh",
    ];

    public async Task<AgentRoleResult> HandleAsync(AgentTask task, CancellationToken ct)
    {
        log.LogInformation("[MedicalSummary] UserId={UserId} Intent={Intent}", task.UserId, task.Intent);

        // Guard against prompt injection in the request itself
        var guard = promptShield.Inspect(task.Input);
        if (!guard.Allowed)
        {
            log.LogWarning("[MedicalSummary] Input blocked by PromptShield: {Reasons}", string.Join(", ", guard.Reasons));
            return new AgentRoleResult(Name, false,
                "Yêu cầu không hợp lệ. Vui lòng mô tả triệu chứng hoặc thông tin cần tóm tắt.",
                new Dictionary<string, string> { ["blocked_reason"] = string.Join(", ", guard.Reasons) });
        }

        task.Context.TryGetValue("audience", out var audience);
        task.Context.TryGetValue("specialty", out var specialty);

        // Build summary using provided context (EHR data already fetched upstream via MCP tools)
        var systemPrompt = BuildSystemPrompt(audience, specialty);
        var summary = await GenerateSummaryAsync(task.Input, systemPrompt, ct).ConfigureAwait(false);

        // Screen output for PII leaks and hallucination patterns
        var outputCheck = outputShield.Inspect(summary);
        var safeOutput = outputCheck.HasLeak ? outputCheck.SafeContent : summary;

        if (outputCheck.HasLeak)
            log.LogWarning("[MedicalSummary] Output shield redacted content: {Detections}",
                string.Join(", ", outputCheck.Detections));

        var summaryId = $"SUM-{DateTimeOffset.UtcNow:yyyyMMdd}-{Guid.CreateVersion7().ToString("N")[..8].ToUpperInvariant()}";
        if (tools.Find("persist_medical_summary") is { } persistTool)
        {
            var patientId = task.Context.GetValueOrDefault("patient_id", task.UserId.ToString());
            var toolCtx = new ToolInvocationContext(task.UserId, task.ConversationId ?? Guid.Empty, task.CorrelationId ?? string.Empty);
            var toolArgs = JsonSerializer.Serialize(new
            {
                summary_id = summaryId,
                patient_id = patientId,
                summary_type = task.Context.GetValueOrDefault("summary_type", task.Intent == "soap_note" ? "soap" : "medical_summary"),
                audience = audience ?? "clinician",
                specialty,
                source_context = task.Input,
                summary_text = safeOutput,
                model = task.Context.GetValueOrDefault("model", string.Empty),
                status = "completed",
            });

            await persistTool.InvokeAsync(toolArgs, toolCtx, ct).ConfigureAwait(false);
        }

        return new AgentRoleResult(
            Role: Name,
            Success: true,
            Output: safeOutput,
            Metadata: new Dictionary<string, string>
            {
                ["summary_id"] = summaryId,
                ["audience"] = audience ?? "clinician",
                ["specialty"] = specialty ?? "general",
                ["output_shielded"] = outputCheck.HasLeak.ToString(),
            });
    }

    private static string BuildSystemPrompt(string? audience, string? specialty)
    {
        var format = (audience?.ToLowerInvariant()) switch
        {
            "patient" =>
                "Viết bằng ngôn ngữ dễ hiểu cho bệnh nhân. Không dùng thuật ngữ y tế chuyên sâu. " +
                "Không đưa ra chẩn đoán mới. Kết thúc bằng: 'Vui lòng xác nhận với bác sĩ trước khi thực hiện bất kỳ thay đổi nào.'",
            _ =>
                "Tạo SOAP note đầy đủ (Subjective/Objective/Assessment/Plan). " +
                $"Chuyên khoa: {specialty ?? "tổng quát"}. " +
                "Đánh dấu ⚠️ bất kỳ tương tác thuốc hoặc dị ứng cần chú ý. " +
                "KHÔNG suy diễn chẩn đoán ngoài dữ liệu cung cấp. " +
                "Nếu thiếu thông tin, ghi 'Chưa có dữ liệu'.",
        };

        return
            "Bạn là AI lâm sàng của Hope.Agent. Chỉ sử dụng thông tin có trong context được cung cấp. " +
            format;
    }

    private async Task<string> GenerateSummaryAsync(string ehrContext, string systemPrompt, CancellationToken ct)
    {
        var chat = llm.SelectChat();
        var response = await chat.CompleteAsync(new ChatRequest(
            [
                new ChatMessage("system", systemPrompt),
                new ChatMessage("user",
                    "Context bệnh án:\n" +
                    ehrContext +
                    "\n\nYêu cầu: tạo bản tóm tắt có cấu trúc, nêu rõ dữ liệu thiếu, không suy diễn ngoài context."),
            ],
            Temperature: 0.2f), ct).ConfigureAwait(false);

        return string.IsNullOrWhiteSpace(response.Content)
            ? "Không tạo được tóm tắt vì mô hình không trả về nội dung."
            : response.Content;
    }
}
