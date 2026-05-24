namespace Hope.Agent.Application.Training;

public sealed record TrajectoryExportFilter(
    DateTimeOffset? Since = null,
    DateTimeOffset? Until = null,
    Guid? UserId = null,
    int? MaxConversations = null,
    int MinTurns = 2,
    bool RedactPhi = true);

public sealed record TrajectoryExportStats(int Conversations, int Messages, long BytesWritten);

public interface ITrajectoryExporter
{
    /// <summary>Writes one JSONL record per conversation to <paramref name="output"/>.</summary>
    Task<TrajectoryExportStats> ExportAsync(TrajectoryExportFilter filter, Stream output, CancellationToken ct);
}

public sealed class TrajectoryExportOptions
{
    public const string Section = "TrajectoryExport";
    public bool Enabled { get; set; }
    public int DefaultMaxConversations { get; set; } = 500;
}
