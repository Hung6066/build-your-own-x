using Hope.Agent.Domain.Memory;

namespace Hope.Agent.Application.Agents;

/// <summary>
/// Reads and writes cross-workflow clinical memories for a patient.
/// Enables agents to recall previous consultations, diagnoses, and treatment notes
/// across independent workflow runs.
/// </summary>
public interface IPatientMemoryService
{
    /// <summary>
    /// Stores a clinical note about the patient as a semantic memory vector.
    /// Content is embedded and upserted — safe to call multiple times with the same content.
    /// Errors are swallowed; memory writes must never break the critical workflow path.
    /// </summary>
    Task WriteAsync(
        Guid patientId,
        string content,
        MemoryKind kind = MemoryKind.Clinical,
        float importance = 0.7f,
        CancellationToken ct = default);

    /// <summary>
    /// Retrieves the most semantically relevant memory contents for a patient given a query.
    /// Returns content strings ordered by similarity descending, or an empty list on failure.
    /// </summary>
    Task<IReadOnlyList<string>> RetrieveAsync(
        Guid patientId,
        string query,
        int topK = 3,
        CancellationToken ct = default);
}
