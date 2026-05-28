namespace Hope.Agent.Application.Caching;

/// <summary>
/// Cache for idempotent tool results. Activated per tool by setting
/// <see cref="Hope.Agent.Application.Tools.IAgentTool.IsCacheable"/> to <c>true</c>.
/// Cache key is <c>(toolName, sha256(argumentsJson), userId)</c> so patient-specific
/// reads never leak across users.
/// </summary>
public interface IToolResultCache
{
    Task<string?> LookupAsync(string toolName, string argsHash, Guid? userId, CancellationToken ct);

    Task StoreAsync(string toolName, string argsHash, Guid? userId, string result,
        TimeSpan ttl, CancellationToken ct);
}

public sealed class NoOpToolResultCache : IToolResultCache
{
    public Task<string?> LookupAsync(string toolName, string argsHash, Guid? userId, CancellationToken ct)
        => Task.FromResult<string?>(null);

    public Task StoreAsync(string toolName, string argsHash, Guid? userId, string result,
        TimeSpan ttl, CancellationToken ct) => Task.CompletedTask;
}
