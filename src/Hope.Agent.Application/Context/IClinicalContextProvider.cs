namespace Hope.Agent.Application.Context;

public sealed record ClinicalContextFragment(string Profile, string Content);

public interface IClinicalContextProvider
{
    /// <summary>Returns merged guidance text for the given agent profile, or null if none configured.</summary>
    Task<ClinicalContextFragment?> GetAsync(string? profile, CancellationToken ct);
    Task<IReadOnlyList<string>> ListProfilesAsync(CancellationToken ct);
}

public sealed class ClinicalContextOptions
{
    public const string Section = "ClinicalContext";
    public bool Enabled { get; set; }
    /// <summary>Folder containing per-profile markdown files named CLINICAL_CONTEXT.{profile}.md and CLINICAL_CONTEXT.md (default).</summary>
    public string Directory { get; set; } = "./context";
    public int CacheSeconds { get; set; } = 60;
    public int MaxCharacters { get; set; } = 4000;
}
