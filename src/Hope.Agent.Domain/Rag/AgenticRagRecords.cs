namespace Hope.Agent.Domain.Rag;

public enum AgenticRagRunStatus
{
    Running = 0,
    Succeeded = 1,
    InsufficientContext = 2,
    Failed = 3,
}

public enum AgenticRagStepKind
{
    Plan = 0,
    Retrieve = 1,
    AssessContext = 2,
    RewriteQuery = 3,
    Synthesize = 4,
}

public sealed class AgenticRagRun
{
    public Guid Id { get; set; }
    public string RunId { get; set; } = string.Empty;
    public Guid? TenantId { get; set; }
    public Guid UserId { get; set; }
    public Guid? PatientId { get; set; }
    public Guid? ConversationId { get; set; }
    public string Query { get; set; } = string.Empty;
    public string Answer { get; set; } = string.Empty;
    public AgenticRagRunStatus Status { get; set; }
    public bool ContextSufficient { get; set; }
    public double Confidence { get; set; }
    public int IterationCount { get; set; }
    public string SelectedCorporaJson { get; set; } = "[]";
    public string CitationsJson { get; set; } = "[]";
    public string MetricsJson { get; set; } = "{}";
    public DateTimeOffset CreatedAt { get; set; }
    public DateTimeOffset? CompletedAt { get; set; }
    public string? CorrelationId { get; set; }
}

public sealed class AgenticRagStep
{
    public Guid Id { get; set; }
    public string StepId { get; set; } = string.Empty;
    public string RunId { get; set; } = string.Empty;
    public AgenticRagStepKind Kind { get; set; }
    public int Iteration { get; set; }
    public string InputJson { get; set; } = "{}";
    public string OutputJson { get; set; } = "{}";
    public DateTimeOffset CreatedAt { get; set; }
    public string? CorrelationId { get; set; }
}

public sealed class AgenticRagRetrieval
{
    public Guid Id { get; set; }
    public string RetrievalId { get; set; } = string.Empty;
    public string RunId { get; set; } = string.Empty;
    public int Iteration { get; set; }
    public string Corpus { get; set; } = string.Empty;
    public string Query { get; set; } = string.Empty;
    public string Source { get; set; } = string.Empty;
    public string ReferenceId { get; set; } = string.Empty;
    public string Title { get; set; } = string.Empty;
    public string Content { get; set; } = string.Empty;
    public string? Url { get; set; }
    public double Score { get; set; }
    public string MetadataJson { get; set; } = "{}";
    public DateTimeOffset CreatedAt { get; set; }
}

public sealed class AgenticRagContextAssessment
{
    public Guid Id { get; set; }
    public string AssessmentId { get; set; } = string.Empty;
    public string RunId { get; set; } = string.Empty;
    public int Iteration { get; set; }
    public bool Sufficient { get; set; }
    public double Confidence { get; set; }
    public string CoveredTermsJson { get; set; } = "[]";
    public string MissingTermsJson { get; set; } = "[]";
    public string Feedback { get; set; } = string.Empty;
    public DateTimeOffset CreatedAt { get; set; }
}
