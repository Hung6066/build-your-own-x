namespace Hope.Agent.Application.Backup;

/// <summary>
/// Coordinates multi-database backup and restore procedures for disaster recovery.
/// Closes gap C-1. Supports PostgreSQL (pgBackRest), Qdrant (snapshot API),
/// Neo4j (neo4j-admin dump), and Kafka (MirrorMaker 2 + S3 archive).
/// </summary>
public interface IBackupOrchestrator
{
    /// <summary>Run a full backup across all configured scopes.</summary>
    Task<BackupResult> RunFullBackupAsync(BackupScope scope, CancellationToken ct);

    /// <summary>Restore all databases to a specific point in time.</summary>
    Task<RestoreResult> RestoreToPointInTimeAsync(DateTimeOffset pointInTime, CancellationToken ct);

    /// <summary>Verify recent backups exist and are healthy.</summary>
    Task<BackupHealth> GetBackupHealthAsync(CancellationToken ct);
}

[Flags]
public enum BackupScope
{
    None = 0,
    Postgres = 1 << 0,
    Qdrant = 1 << 1,
    Neo4j = 1 << 2,
    Redis = 1 << 3,
    All = Postgres | Qdrant | Neo4j | Redis
}

public sealed record BackupResult(
    BackupScope CompletedScopes,
    BackupScope FailedScopes,
    IReadOnlyDictionary<BackupScope, string> ArtifactPaths,
    DateTimeOffset StartedAt,
    TimeSpan Duration,
    long TotalBytesBackedUp);

public sealed record RestoreResult(
    BackupScope RestoredScopes,
    bool Success,
    DateTimeOffset PointInTime,
    TimeSpan Duration,
    IReadOnlyList<string> Warnings);

public sealed record BackupHealth(
    bool IsHealthy,
    DateTimeOffset? LastFullBackup,
    DateTimeOffset? LastWalArchive,
    IReadOnlyDictionary<BackupScope, DateTimeOffset> LastBackupPerScope,
    IReadOnlyList<string> Issues);
