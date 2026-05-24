using System.Collections.Concurrent;
using Hope.Agent.Application.Context;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace Hope.Agent.Infrastructure.Context;

internal sealed class FileClinicalContextProvider(
    IOptions<ClinicalContextOptions> opts,
    ILogger<FileClinicalContextProvider> log) : IClinicalContextProvider
{
    private readonly ConcurrentDictionary<string, CacheEntry> _cache = new(StringComparer.OrdinalIgnoreCase);

    public Task<ClinicalContextFragment?> GetAsync(string? profile, CancellationToken ct)
    {
        var o = opts.Value;
        if (!o.Enabled) return Task.FromResult<ClinicalContextFragment?>(null);

        var key = string.IsNullOrWhiteSpace(profile) ? "_default" : profile;
        if (_cache.TryGetValue(key, out var entry) &&
            entry.LoadedAt.AddSeconds(o.CacheSeconds) > DateTimeOffset.UtcNow)
        {
            return Task.FromResult(entry.Fragment);
        }

        var content = ReadProfile(o.Directory, profile, o.MaxCharacters);
        var frag = content is null ? null : new ClinicalContextFragment(profile ?? "_default", content);
        _cache[key] = new CacheEntry(frag, DateTimeOffset.UtcNow);
        return Task.FromResult(frag);
    }

    public Task<IReadOnlyList<string>> ListProfilesAsync(CancellationToken ct)
    {
        var o = opts.Value;
        if (!o.Enabled || !Directory.Exists(o.Directory))
            return Task.FromResult<IReadOnlyList<string>>(Array.Empty<string>());
        var files = Directory.EnumerateFiles(o.Directory, "CLINICAL_CONTEXT.*.md");
        var profiles = files
            .Select(Path.GetFileNameWithoutExtension)
            .Where(n => n is not null)
            .Select(n => n!.Replace("CLINICAL_CONTEXT.", string.Empty, StringComparison.Ordinal))
            .OrderBy(n => n)
            .ToList();
        return Task.FromResult<IReadOnlyList<string>>(profiles);
    }

    private string? ReadProfile(string dir, string? profile, int maxChars)
    {
        try
        {
            if (!Directory.Exists(dir)) return null;
            var parts = new List<string>(capacity: 2);
            var defaultPath = Path.Combine(dir, "CLINICAL_CONTEXT.md");
            if (File.Exists(defaultPath)) parts.Add(File.ReadAllText(defaultPath));
            if (!string.IsNullOrWhiteSpace(profile))
            {
                var profilePath = Path.Combine(dir, $"CLINICAL_CONTEXT.{profile}.md");
                if (File.Exists(profilePath)) parts.Add(File.ReadAllText(profilePath));
            }
            if (parts.Count == 0) return null;
            var combined = string.Join("\n\n---\n\n", parts).Trim();
            if (combined.Length > maxChars) combined = combined[..maxChars] + "\n…[truncated]";
            return combined;
        }
        catch (Exception ex)
        {
            log.LogWarning(ex, "Failed to read clinical context for profile {Profile}.", profile);
            return null;
        }
    }

    private sealed record CacheEntry(ClinicalContextFragment? Fragment, DateTimeOffset LoadedAt);
}
