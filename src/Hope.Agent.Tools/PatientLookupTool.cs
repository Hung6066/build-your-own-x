using System.Text.Json;
using System.Net.Http;
using Hope.Agent.Application.LLM;
using Hope.Agent.Application.Tools;
using Microsoft.Extensions.Options;

namespace Hope.Agent.Tools;

public sealed class PatientLookupTool(
    IOptions<ClinicalIntegrationOptions>? options = null,
    IHttpClientFactory? httpFactory = null) : IAgentTool
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

    public async Task<string> InvokeAsync(string argumentsJson, ToolInvocationContext context, CancellationToken ct)
    {
        var args = JsonDocument.Parse(argumentsJson).RootElement;
        var pid = args.GetProperty("patient_id").GetString() ?? string.Empty;
        var cfg = options?.Value;

        if (!string.IsNullOrWhiteSpace(cfg?.FhirBaseUrl) && httpFactory is not null)
        {
            var client = httpFactory.CreateClient("clinical-integrations");
            if (!string.IsNullOrWhiteSpace(cfg.FhirApiKey))
                client.DefaultRequestHeaders.Authorization = new("Bearer", cfg.FhirApiKey);

            var baseUrl = cfg.FhirBaseUrl.TrimEnd('/');
            using var response = await client.GetAsync($"{baseUrl}/Patient/{Uri.EscapeDataString(pid)}", ct).ConfigureAwait(false);
            if (response.IsSuccessStatusCode)
            {
                var raw = await response.Content.ReadAsStringAsync(ct).ConfigureAwait(false);
                return JsonSerializer.Serialize(new
                {
                    patient_id = pid,
                    source = "fhir",
                    fhir_resource = JsonSerializer.Deserialize<JsonElement>(raw),
                });
            }
        }

        var result = JsonSerializer.Serialize(new
        {
            patient_id = pid,
            source = "fixture",
            name = "Nguyen Van A",
            dob = "1985-03-12",
            sex = "M",
            alerts = new[] { "penicillin_allergy" },
        });
        return result;
    }
}
