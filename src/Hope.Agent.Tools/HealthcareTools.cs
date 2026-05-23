using System.Text.Json;
using Hope.Agent.Application.LLM;
using Hope.Agent.Application.Rag;
using Hope.Agent.Application.Tools;

namespace Hope.Agent.Tools;

public sealed class AppointmentScheduleTool : IAgentTool
{
    public ToolDefinition Definition { get; } = new(
        "schedule_appointment",
        "Schedule a clinical appointment. Returns the booking confirmation.",
        """
        {
          "type": "object",
          "properties": {
            "patient_id": {"type": "string"},
            "department": {"type": "string"},
            "preferred_time": {"type": "string", "format": "date-time"},
            "reason": {"type": "string"}
          },
          "required": ["patient_id", "department", "preferred_time"]
        }
        """);

    public Task<string> InvokeAsync(string argumentsJson, ToolInvocationContext context, CancellationToken ct)
    {
        var args = JsonDocument.Parse(argumentsJson).RootElement;
        var result = JsonSerializer.Serialize(new
        {
            booking_id = Guid.NewGuid().ToString("N")[..10],
            status = "confirmed",
            patient_id = args.GetProperty("patient_id").GetString(),
            department = args.GetProperty("department").GetString(),
            time = args.GetProperty("preferred_time").GetString(),
        });
        return Task.FromResult(result);
    }
}

public sealed class InsuranceVerifyTool : IAgentTool
{
    public ToolDefinition Definition { get; } = new(
        "verify_insurance",
        "Verify insurance coverage for a patient and procedure code.",
        """
        {
          "type": "object",
          "properties": {
            "patient_id": {"type": "string"},
            "procedure_code": {"type": "string"}
          },
          "required": ["patient_id", "procedure_code"]
        }
        """);

    public Task<string> InvokeAsync(string argumentsJson, ToolInvocationContext context, CancellationToken ct)
    {
        var args = JsonDocument.Parse(argumentsJson).RootElement;
        var result = JsonSerializer.Serialize(new
        {
            patient_id = args.GetProperty("patient_id").GetString(),
            procedure_code = args.GetProperty("procedure_code").GetString(),
            covered = true,
            coverage_percent = 80,
            policy_number = "BHYT-2026-XXXX",
        });
        return Task.FromResult(result);
    }
}

public sealed class ClinicalGuidelineSearchTool(IRetriever retriever) : IAgentTool
{
    public ToolDefinition Definition { get; } = new(
        "search_clinical_guidelines",
        "Search internal clinical guidelines, SOPs, and protocols. Returns top relevant excerpts.",
        """
        {
          "type": "object",
          "properties": {
            "query": {"type": "string"},
            "top_k": {"type": "integer", "default": 5}
          },
          "required": ["query"]
        }
        """);

    public async Task<string> InvokeAsync(string argumentsJson, ToolInvocationContext context, CancellationToken ct)
    {
        var args = JsonDocument.Parse(argumentsJson).RootElement;
        var query = args.GetProperty("query").GetString() ?? string.Empty;
        var topK = args.TryGetProperty("top_k", out var k) ? k.GetInt32() : 5;
        var hits = await retriever.SearchAsync(new RetrievalQuery(query, "clinical_guidelines", TopK: Math.Max(topK, 4) * 2, FinalK: topK), ct);
        return JsonSerializer.Serialize(new
        {
            query,
            hits = hits.Select(h => new
            {
                title = h.Title,
                url = h.Url,
                score = h.Score,
                content = h.Content,
            }),
        });
    }
}
