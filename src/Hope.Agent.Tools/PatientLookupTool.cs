using System.Text.Json;
using Hope.Agent.Application.LLM;
using Hope.Agent.Application.Tools;

namespace Hope.Agent.Tools;

public sealed class PatientLookupTool : IAgentTool
{
    public ToolDefinition Definition { get; } = new(
        "patient_lookup",
        "Look up a patient by national ID or medical record number. Returns demographics + active alerts.",
        """
        {
          "type": "object",
          "properties": {
            "patient_id": {"type": "string", "description": "MRN or national ID"}
          },
          "required": ["patient_id"]
        }
        """);

    public Task<string> InvokeAsync(string argumentsJson, ToolInvocationContext context, CancellationToken ct)
    {
        var args = JsonDocument.Parse(argumentsJson).RootElement;
        var pid = args.GetProperty("patient_id").GetString();
        // TODO: wire to HIS/EHR. Stub returns deterministic fixture for now.
        var result = JsonSerializer.Serialize(new
        {
            patient_id = pid,
            name = "Nguyen Van A",
            dob = "1985-03-12",
            sex = "M",
            alerts = new[] { "penicillin_allergy" },
        });
        return Task.FromResult(result);
    }
}
