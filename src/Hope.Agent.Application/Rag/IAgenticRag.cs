using Hope.Agent.Domain.Rag;

namespace Hope.Agent.Application.Rag;

public sealed class AgenticRagOptions
{
    public const string SectionName = "AgenticRag";
    public bool Enabled { get; init; } = true;
    public int MaxIterations { get; init; } = 3;
    public int MaxCorporaPerRun { get; init; } = 6;
    public int TopKPerCorpus { get; init; } = 8;
    public int FinalKPerCorpus { get; init; } = 4;
    public int MaxContextChars { get; init; } = 12_000;
    public double MinSufficiencyConfidence { get; init; } = 0.62;
    public bool RequireCitations { get; init; } = true;
    public bool TenantScopedRetrievalRequired { get; init; } = true;
    public bool CacheRetrievalResults { get; init; } = true;
    public string[] StopWords { get; init; } =
    [
        "the", "and", "or", "for", "with", "from", "that", "this", "what", "which", "when", "where", "why", "how",
        "cua", "của", "và", "hoặc", "cho", "với", "trong", "ngoài", "bệnh", "nhân", "hãy", "là", "có", "không"
    ];
    public Dictionary<string, AgenticRagCorpusOptions> Corpora { get; init; } = new(StringComparer.OrdinalIgnoreCase);
}

public sealed class AgenticRagCorpusOptions
{
    public string Description { get; init; } = string.Empty;
    public string Type { get; init; } = "document";
    public string Collection { get; init; } = "clinical_guidelines";
    public double SourceWeight { get; init; } = 1.0;
    public bool RequiresPatientId { get; init; }
    public bool Enabled { get; init; } = true;
}

public sealed record AgenticRagRequest(
    string Query,
    Guid UserId,
    Guid? TenantId = null,
    Guid? PatientId = null,
    Guid? ConversationId = null,
    string? Goal = null,
    string[]? Corpora = null,
    int? MaxIterations = null,
    string? CorrelationId = null);

public sealed record AgenticRagCitation(
    string Corpus,
    string Source,
    string ReferenceId,
    string Title,
    string Excerpt,
    string? Url,
    double Score);

public sealed record AgenticRagContextAssessmentResult(
    bool Sufficient,
    double Confidence,
    IReadOnlyList<string> CoveredTerms,
    IReadOnlyList<string> MissingTerms,
    string Feedback);

public sealed record AgenticRagResult(
    string RunId,
    AgenticRagRunStatus Status,
    bool ContextSufficient,
    double Confidence,
    string Answer,
    IReadOnlyList<AgenticRagCitation> Citations,
    IReadOnlyList<string> SelectedCorpora,
    IReadOnlyList<string> MissingTerms,
    int Iterations);

public sealed record AgenticRagRunTrace(
    AgenticRagRun Run,
    IReadOnlyList<AgenticRagStep> Steps,
    IReadOnlyList<AgenticRagRetrieval> Retrievals,
    IReadOnlyList<AgenticRagContextAssessment> Assessments);

public interface IAgenticRagService
{
    Task<AgenticRagResult> RunAsync(AgenticRagRequest request, CancellationToken ct);
    Task<AgenticRagRunTrace?> GetTraceAsync(string runId, CancellationToken ct);
}
