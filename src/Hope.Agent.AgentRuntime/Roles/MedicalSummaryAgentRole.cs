using Hope.Agent.Application.Agents.Multi;
using Hope.Agent.Application.Rag;
using Hope.Agent.Application.Security;
using Hope.Agent.Application.LLM;
using Microsoft.Extensions.Logging;

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
    private readonly ILogger<MedicalSummaryAgentRole> log;

    public MedicalSummaryAgentRole(
        IRetrievalRail retrievalRail,
        IOutputShield outputShield,
        IPromptShield promptShield,
        ILogger<MedicalSummaryAgentRole> log)
    {
        this.retrievalRail = retrievalRail;
        this.outputShield = outputShield;
        this.promptShield = promptShield;
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

        return new AgentRoleResult(
            Role: Name,
            Success: true,
            Output: safeOutput,
            Metadata: new Dictionary<string, string>
            {
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

    private static Task<string> GenerateSummaryAsync(string ehrContext, string systemPrompt, CancellationToken ct)
    {
        // Actual LLM call is delegated to AgentOrchestrator via tool-calling pipeline.
        // This role provides the structured system prompt and validated context;
        // the orchestrator invokes the LLM and returns the result back here.
        // Returning a structured placeholder that the orchestrator will replace.
        _ = ct;
        return Task.FromResult(
            $"[SYSTEM: {systemPrompt}]\n\n" +
            $"[EHR CONTEXT]\n{ehrContext}\n\n" +
            "[Tóm tắt bệnh án sẽ được LLM điền vào đây sau khi qua pipeline của AgentOrchestrator]");
    }
}
