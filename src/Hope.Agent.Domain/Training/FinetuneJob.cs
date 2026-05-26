namespace Hope.Agent.Domain.Training;

public enum FinetuneJobStatus
{
    Pending,
    Preparing,
    Training,
    Evaluating,
    Completed,
    Failed,
    Cancelled,
}

public enum FinetuneJobType
{
    Sft,
    Dpo,
    SftThenDpo,
}

/// <summary>Tracks a single LoRA fine-tune training job submitted to the Python training service.</summary>
public sealed class FinetuneJob
{
    public Guid Id { get; init; }

    public FinetuneJobType JobType { get; init; } = FinetuneJobType.Dpo;
    public FinetuneJobStatus Status { get; set; } = FinetuneJobStatus.Pending;

    /// <summary>Base model to fine-tune (e.g. "Qwen/Qwen3-8B").</summary>
    public required string BaseModel { get; init; }

    /// <summary>Output adapter tag / Ollama model name after registration.</summary>
    public string? OutputModelTag { get; set; }

    /// <summary>Date range of training data included in this job.</summary>
    public DateTimeOffset DataSince { get; init; }
    public DateTimeOffset DataUntil { get; init; }

    /// <summary>Number of preference pairs / trajectories included.</summary>
    public int RecordCount { get; set; }

    /// <summary>Remote job ID returned by the Python training API.</summary>
    public string? RemoteJobId { get; set; }

    /// <summary>JSON progress snapshot returned by the training API.</summary>
    public string? ProgressJson { get; set; }

    /// <summary>Elo score of the fine-tuned model after auto-evaluation. Null until evaluated.</summary>
    public double? EloScore { get; set; }

    /// <summary>Error detail if Status == Failed.</summary>
    public string? ErrorDetail { get; set; }

    public Guid CreatedByUserId { get; init; }
    public DateTimeOffset CreatedAt { get; init; }
    public DateTimeOffset? StartedAt { get; set; }
    public DateTimeOffset? FinishedAt { get; set; }
}
