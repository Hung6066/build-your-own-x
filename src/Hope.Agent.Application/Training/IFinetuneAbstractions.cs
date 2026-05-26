using Hope.Agent.Domain.Training;

namespace Hope.Agent.Application.Training;

public interface IPreferenceStore
{
    Task AddAsync(PreferenceRecord record, CancellationToken ct);

    Task<IReadOnlyList<PreferenceRecord>> QueryAsync(
        DateTimeOffset? since = null,
        DateTimeOffset? until = null,
        string? specialty = null,
        int take = 200,
        CancellationToken ct = default);

    Task<int> CountAsync(DateTimeOffset? since = null, CancellationToken ct = default);
}

public interface IDpoExporter
{
    /// <summary>
    /// Writes one JSONL line per preference pair in DPO format:
    /// { "prompt": [...messages], "chosen": [...], "rejected": [...] }
    /// Compatible with TRL's DPOTrainer and HuggingFace datasets.
    /// </summary>
    Task<DpoExportStats> ExportAsync(DpoExportFilter filter, Stream output, CancellationToken ct);
}

public sealed record DpoExportFilter(
    DateTimeOffset? Since = null,
    DateTimeOffset? Until = null,
    string? Specialty = null,
    int MaxRecords = 5_000,
    bool RedactPhi = true);

public sealed record DpoExportStats(int Records, long BytesWritten);

public interface IFinetuneJobService
{
    /// <summary>Submits a new fine-tune job to the Python training API and persists the tracker.</summary>
    Task<FinetuneJob> SubmitAsync(FinetuneJobRequest request, CancellationToken ct);

    /// <summary>Polls the remote training API and updates the local job record.</summary>
    Task<FinetuneJob> RefreshStatusAsync(Guid jobId, CancellationToken ct);

    Task<FinetuneJob?> GetAsync(Guid jobId, CancellationToken ct);

    Task<IReadOnlyList<FinetuneJob>> ListAsync(int take = 20, CancellationToken ct = default);

    /// <summary>Cancels a running job both remotely and locally.</summary>
    Task CancelAsync(Guid jobId, CancellationToken ct);
}

public sealed record FinetuneJobRequest(
    FinetuneJobType JobType,
    string BaseModel,
    DateTimeOffset DataSince,
    DateTimeOffset DataUntil,
    Guid CreatedByUserId,
    string? OutputModelTag = null,
    int? MaxRecords = null);

public sealed class FineTuningOptions
{
    public const string Section = "FineTuning";

    /// <summary>Base URL of the Python training API (e.g. http://localhost:8765).</summary>
    public string TrainingApiUrl { get; set; } = "http://localhost:8765";

    /// <summary>API key sent in the X-Api-Key header to the Python service.</summary>
    public string ApiKey { get; set; } = string.Empty;

    public string DefaultBaseModel { get; set; } = "Qwen/Qwen3-8B";

    /// <summary>Elo threshold above which a fine-tuned adapter is automatically registered.</summary>
    public double AutoRegisterEloThreshold { get; set; } = 1050.0;
}
