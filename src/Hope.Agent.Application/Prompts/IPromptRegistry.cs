namespace Hope.Agent.Application.Prompts;

/// <summary>
/// Centralized prompt registry with versioning and tenant-scoped templates.
/// Closes gap C-7. Enables A/B testing of prompt versions, shadow comparisons,
/// and gradual rollouts. Templates can be stored in Git (filesystem) or
/// PostgreSQL for hot-reload without redeploy.
/// </summary>
public interface IPromptRegistry
{
    /// <summary>Retrieve a prompt template by name, optionally pinned to a version.</summary>
    Task<PromptTemplate> GetAsync(string name, string? version = null, CancellationToken ct = default);

    /// <summary>
    /// Retrieve the best prompt template for a given tenant + intent combination.
    /// Respects tenant-specific overrides and A/B test assignments.
    /// </summary>
    Task<PromptTemplate> GetForTenantAsync(Guid tenantId, string intent, CancellationToken ct = default);

    /// <summary>Register a new prompt template version.</summary>
    Task RegisterAsync(PromptTemplate template, CancellationToken ct = default);

    /// <summary>List all versions of a named prompt.</summary>
    Task<IReadOnlyList<PromptTemplate>> ListVersionsAsync(string name, CancellationToken ct = default);

    /// <summary>Activate a specific version (makes it the default for new requests).</summary>
    Task ActivateVersionAsync(string name, string version, CancellationToken ct = default);
}

/// <summary>Immutable prompt template with content-hash versioning.</summary>
public sealed record PromptTemplate(
    Guid Id,
    string Name,
    string Version,          // SHA-256 of Content
    string Content,
    IReadOnlyList<string> Tags,
    Guid? TenantId,           // null = global, non-null = tenant override
    DateTimeOffset CreatedAt,
    string CreatedBy,
    bool Active);

/// <summary>Configuration for the prompt registry backend.</summary>
public sealed class PromptRegistryOptions
{
    public const string Section = "PromptRegistry";

    /// <summary>Storage backend: "postgres" or "filesystem".</summary>
    public string Backend { get; set; } = "filesystem";

    /// <summary>Filesystem root directory for Git-based prompt storage.</summary>
    public string FilesystemRoot { get; set; } = "prompts";

    /// <summary>When true, watches the filesystem for changes and hot-reloads.</summary>
    public bool EnableHotReload { get; set; } = true;

    /// <summary>Interval for filesystem polling when hot-reload is enabled.</summary>
    public TimeSpan HotReloadInterval { get; set; } = TimeSpan.FromSeconds(30);
}
