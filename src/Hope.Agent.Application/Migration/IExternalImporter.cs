namespace Hope.Agent.Application.Migration;

public enum ExternalSource
{
    Unknown = 0,
    DialogflowFaq = 1,
    Rasa = 2,
    GenericFaq = 3,
}

public sealed record ImportRequest(
    ExternalSource Source,
    Stream Payload,
    string? Intent = null,
    bool DryRun = false);

public sealed record ImportStats(int Items, int Imported, int Skipped, IReadOnlyList<string> Warnings);

public interface IExternalImporter
{
    Task<ImportStats> ImportAsync(ImportRequest request, CancellationToken ct);
}

public sealed class MigrationOptions
{
    public const string Section = "Migration";
    public bool Enabled { get; set; }
    public int MaxItemsPerImport { get; set; } = 5000;
}
