using System.Text.Json;

namespace Hope.Agent.Application.Fhir;

/// <summary>
/// FHIR R4 resource validation for healthcare data interchange.
/// Closes gap H-1. Validates incoming/outgoing payloads against FHIR
/// profiles (Patient, Observation, Condition, MedicationRequest, etc.)
/// and performs terminology binding checks (SNOMED CT, LOINC, ICD-10).
/// </summary>
public interface IFhirValidator
{
    /// <summary>Validate a FHIR resource payload against the specified resource type profile.</summary>
    Task<FhirValidationResult> ValidateAsync(
        string resourceType,
        JsonDocument payload,
        CancellationToken ct = default);

    /// <summary>Validate and normalize a FHIR resource, returning the canonical JSON.</summary>
    Task<FhirValidationResult> ValidateAndNormalizeAsync(
        string resourceType,
        JsonDocument payload,
        CancellationToken ct = default);

    /// <summary>List the FHIR resource types supported by this validator.</summary>
    IReadOnlySet<string> SupportedResourceTypes { get; }
}

public sealed record FhirValidationResult(
    bool IsValid,
    string? NormalizedJson,
    IReadOnlyList<FhirValidationError> Errors,
    IReadOnlyList<FhirValidationWarning> Warnings);

public sealed record FhirValidationError(
    string Field,
    string Code,
    string Message,
    FhirErrorSeverity Severity = FhirErrorSeverity.Error);

public sealed record FhirValidationWarning(
    string Field,
    string Code,
    string Message);

public enum FhirErrorSeverity
{
    Warning,
    Error,
    Fatal
}
