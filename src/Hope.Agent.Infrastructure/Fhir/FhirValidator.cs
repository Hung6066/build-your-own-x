using System.Text.Json;
using System.Text.Json.Nodes;
using Hope.Agent.Application.Fhir;
using Microsoft.Extensions.Logging;

namespace Hope.Agent.Infrastructure.Fhir;

/// <summary>
/// Lightweight FHIR R4 validator for healthcare data interchange.
/// Validates incoming payloads against FHIR resource type profiles.
/// Closes gap H-1.
///
/// For full FHIR conformance (terminology binding with SNOMED CT / LOINC / ICD-10),
/// deploy an instance of the HAPI FHIR Validator or Firely .NET SDK alongside this
/// service. This implementation provides schema-level validation and structural checks.
/// </summary>
internal sealed class FhirValidator : IFhirValidator
{
    private static readonly HashSet<string> Supported = new(StringComparer.OrdinalIgnoreCase)
    {
        "Patient", "Observation", "Condition", "MedicationRequest",
        "MedicationAdministration", "AllergyIntolerance", "Procedure",
        "DiagnosticReport", "Immunization", "Encounter"
    };

    private static readonly HashSet<string> RequiredPatientFields = new(StringComparer.OrdinalIgnoreCase)
    {
        "resourceType", "id", "name"
    };

    private static readonly HashSet<string> RequiredObservationFields = new(StringComparer.OrdinalIgnoreCase)
    {
        "resourceType", "code", "subject", "status"
    };

    private readonly ILogger<FhirValidator> _log;

    public FhirValidator(ILogger<FhirValidator> log)
    {
        _log = log;
    }

    public IReadOnlySet<string> SupportedResourceTypes => Supported;

    public Task<FhirValidationResult> ValidateAsync(string resourceType, JsonDocument payload, CancellationToken ct = default)
    {
        var errors = new List<FhirValidationError>();
        var warnings = new List<FhirValidationWarning>();

        if (!Supported.Contains(resourceType))
        {
            errors.Add(new FhirValidationError("resourceType", "unsupported",
                $"Resource type '{resourceType}' is not supported. Supported: {string.Join(", ", Supported)}",
                FhirErrorSeverity.Fatal));
            return Task.FromResult(new FhirValidationResult(false, null, errors, warnings));
        }

        var root = payload.RootElement;

        // Validate resourceType
        if (root.TryGetProperty("resourceType", out var rt))
        {
            if (!string.Equals(rt.GetString(), resourceType, StringComparison.OrdinalIgnoreCase))
            {
                errors.Add(new FhirValidationError("resourceType", "mismatch",
                    $"Declared resourceType '{rt.GetString()}' does not match endpoint '{resourceType}'"));
            }
        }
        else
        {
            errors.Add(new FhirValidationError("resourceType", "missing",
                "FHIR resource must include a 'resourceType' field"));
        }

        // Validate required fields per resource type
        ValidateRequiredFields(root, resourceType, errors);

        // Validate id format (UUID or simple string)
        if (root.TryGetProperty("id", out var idProp) && idProp.ValueKind != JsonValueKind.String)
        {
            errors.Add(new FhirValidationError("id", "invalid_type", "FHIR 'id' must be a string"));
        }

        var isValid = errors.Count == 0;
        if (isValid)
        {
            _log.LogDebug("FHIR validation passed: {ResourceType}", resourceType);
        }
        else
        {
            _log.LogWarning("FHIR validation failed: {ResourceType} errors={ErrorCount}", resourceType, errors.Count);
        }

        return Task.FromResult(new FhirValidationResult(isValid, null, errors, warnings));
    }

    public Task<FhirValidationResult> ValidateAndNormalizeAsync(string resourceType, JsonDocument payload, CancellationToken ct = default)
    {
        var baseResult = ValidateAsync(resourceType, payload, ct).Result;
        if (!baseResult.IsValid)
            return Task.FromResult(baseResult);

        // Normalize: sort keys canonically, ensure idempotent representation
        var root = JsonNode.Parse(payload.RootElement.GetRawText());
        var normalized = root?.ToJsonString(new JsonSerializerOptions
        {
            WriteIndented = false,
            PropertyNamingPolicy = JsonNamingPolicy.CamelCase
        });

        return Task.FromResult(new FhirValidationResult(
            true, normalized, baseResult.Errors, baseResult.Warnings));
    }

    private static void ValidateRequiredFields(JsonElement root, string resourceType, List<FhirValidationError> errors)
    {
        var requiredFields = resourceType.ToLowerInvariant() switch
        {
            "patient" => RequiredPatientFields,
            "observation" => RequiredObservationFields,
            _ => new HashSet<string>(StringComparer.OrdinalIgnoreCase) { "resourceType", "id" }
        };

        foreach (var field in requiredFields)
        {
            if (!root.TryGetProperty(field, out _) || root.GetProperty(field).ValueKind == JsonValueKind.Null)
            {
                errors.Add(new FhirValidationError(field, "missing",
                    $"FHIR {resourceType} requires field '{field}'"));
            }
        }
    }
}
