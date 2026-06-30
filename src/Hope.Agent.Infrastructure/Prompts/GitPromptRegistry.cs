using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Hope.Agent.Application.Prompts;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace Hope.Agent.Infrastructure.Prompts;

/// <summary>
/// File-system backed prompt registry with Git-friendly layout and hot-reload support.
/// Closes gap C-7.
///
/// Directory structure:
///   prompts/
///     scheduling/system-prompt.v1.txt    (SHA256: a1b2c3...)
///     scheduling/system-prompt.v2.txt
///     medical-summary/system-prompt.v1.txt
///     insurance/system-prompt.v1.txt
///
/// Each .txt file is a raw prompt template. Version is derived from SHA-256 of content.
/// Hot-reload watches the filesystem and refreshes the in-memory index.
/// </summary>
internal sealed class GitPromptRegistry : IPromptRegistry, IDisposable
{
    private readonly PromptRegistryOptions _options;
    private readonly ILogger<GitPromptRegistry> _log;
    private readonly Dictionary<string, SortedList<string, PromptTemplate>> _index = new(StringComparer.OrdinalIgnoreCase);
    private readonly SemaphoreSlim _lock = new(1, 1);
    private FileSystemWatcher? _watcher;
    private DateTimeOffset _lastReload = DateTimeOffset.MinValue;

    public GitPromptRegistry(IOptions<PromptRegistryOptions> options, ILogger<GitPromptRegistry> log)
    {
        _options = options.Value;
        _log = log;
    }

    public async Task<PromptTemplate> GetAsync(string name, string? version = null, CancellationToken ct = default)
    {
        await EnsureIndexAsync(ct);
        await _lock.WaitAsync(ct);
        try
        {
            if (!_index.TryGetValue(name, out var versions) || versions.Count == 0)
                throw new KeyNotFoundException($"Prompt '{name}' not found");

            if (version is not null)
            {
                if (versions.TryGetValue(version, out var pinned))
                    return pinned;
                throw new KeyNotFoundException($"Prompt '{name}' version '{version}' not found");
            }

            // Return the latest active version
            return versions.Values.Last(v => v.Active);
        }
        finally
        {
            _lock.Release();
        }
    }

    public async Task<PromptTemplate> GetForTenantAsync(Guid tenantId, string intent, CancellationToken ct = default)
    {
        await EnsureIndexAsync(ct);
        await _lock.WaitAsync(ct);
        try
        {
            // First check for tenant-specific override
            if (_index.TryGetValue(intent, out var versions))
            {
                var tenantOverride = versions.Values
                    .Where(v => v.TenantId == tenantId && v.Active)
                    .MaxBy(v => v.CreatedAt);
                if (tenantOverride is not null)
                    return tenantOverride;
            }

            // Fall back to global default
            return await GetAsync(intent, version: null, ct);
        }
        finally
        {
            _lock.Release();
        }
    }

    public Task RegisterAsync(PromptTemplate template, CancellationToken ct = default)
    {
        var dir = Path.Combine(_options.FilesystemRoot, template.Name);
        Directory.CreateDirectory(dir);
        var fileName = $"{template.Name}.{template.Version[..8]}.txt";
        File.WriteAllText(Path.Combine(dir, fileName), template.Content, Encoding.UTF8);
        _log.LogInformation("Prompt registered: {Name} v{Version}", template.Name, template.Version[..8]);
        return Task.CompletedTask;
    }

    public async Task<IReadOnlyList<PromptTemplate>> ListVersionsAsync(string name, CancellationToken ct = default)
    {
        await EnsureIndexAsync(ct);
        await _lock.WaitAsync(ct);
        try
        {
            if (_index.TryGetValue(name, out var versions))
                return versions.Values.ToList();
            return Array.Empty<PromptTemplate>();
        }
        finally
        {
            _lock.Release();
        }
    }

    public async Task ActivateVersionAsync(string name, string version, CancellationToken ct = default)
    {
        await EnsureIndexAsync(ct);
        await _lock.WaitAsync(ct);
        try
        {
            if (!_index.TryGetValue(name, out var versions))
                throw new KeyNotFoundException($"Prompt '{name}' not found");

            // Deactivate all versions, activate the requested one
            foreach (var v in versions.Values)
                _index[name][v.Version] = v with { Active = false };

            if (versions.TryGetValue(version, out var target))
                _index[name][version] = target with { Active = true };

            _log.LogInformation("Prompt '{Name}' activated version {Version}", name, version[..8]);
        }
        finally
        {
            _lock.Release();
        }
    }

    private async Task EnsureIndexAsync(CancellationToken ct)
    {
        var reloadNeeded = _lastReload == DateTimeOffset.MinValue;
        if (!reloadNeeded) return;

        await _lock.WaitAsync(ct);
        try
        {
            if (_lastReload != DateTimeOffset.MinValue) return; // Double-check after lock
            LoadFromFilesystem();
            _lastReload = DateTimeOffset.UtcNow;

            if (_options.EnableHotReload)
                StartFileWatcher();
        }
        finally
        {
            _lock.Release();
        }
    }

    private void LoadFromFilesystem()
    {
        _index.Clear();
        var root = _options.FilesystemRoot;
        if (!Directory.Exists(root))
        {
            _log.LogWarning("Prompt registry filesystem root not found: {Root}", root);
            return;
        }

        var count = 0;
        foreach (var dir in Directory.GetDirectories(root))
        {
            var name = Path.GetFileName(dir);
            var versions = new SortedList<string, PromptTemplate>(StringComparer.OrdinalIgnoreCase);

            foreach (var file in Directory.GetFiles(dir, "*.txt"))
            {
                var content = File.ReadAllText(file, Encoding.UTF8);
                var hash = ComputeHash(content);
                var isLatest = !versions.ContainsKey(hash);

                versions[hash] = new PromptTemplate(
                    Guid.CreateVersion7(),
                    name,
                    hash,
                    content,
                    Array.Empty<string>(),
                    null, // global
                    File.GetLastWriteTimeUtc(file),
                    "git",
                    isLatest); // only latest is active
            }

            if (versions.Count > 0)
            {
                _index[name] = versions;
                count += versions.Count;
            }
        }

        _log.LogInformation("Prompt registry loaded: {PromptCount} prompts in {NameCount} namespaces", count, _index.Count);
    }

    private void StartFileWatcher()
    {
        if (!Directory.Exists(_options.FilesystemRoot)) return;

        _watcher = new FileSystemWatcher(_options.FilesystemRoot, "*.txt")
        {
            IncludeSubdirectories = true,
            NotifyFilter = NotifyFilters.LastWrite | NotifyFilters.FileName | NotifyFilters.CreationTime
        };

        _watcher.Changed += OnFileChanged;
        _watcher.Created += OnFileChanged;
        _watcher.Deleted += OnFileChanged;
        _watcher.EnableRaisingEvents = true;
    }

    private void OnFileChanged(object _, FileSystemEventArgs e)
    {
        // Debounce: reload after 5s of no activity
        _ = Task.Run(async () =>
        {
            await Task.Delay(5000);
            await _lock.WaitAsync();
            try
            {
                LoadFromFilesystem();
                _lastReload = DateTimeOffset.UtcNow;
                _log.LogInformation("Prompt registry hot-reloaded: {File}", e.Name);
            }
            finally
            {
                _lock.Release();
            }
        });
    }

    private static string ComputeHash(string content)
        => Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(content))).ToLowerInvariant();

    public void Dispose()
    {
        _watcher?.Dispose();
        _lock?.Dispose();
    }
}
