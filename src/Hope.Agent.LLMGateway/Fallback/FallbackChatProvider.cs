using System.Text.Json;
using Hope.Agent.Application.LLM;
using Microsoft.Extensions.Logging;

namespace Hope.Agent.LLMGateway.Fallback;

/// <summary>
/// Provider fallback chain: tries providers in configured order, falling through
/// on transient failures (rate-limit, timeout) until one succeeds or all are exhausted.
/// Closes gap H-5. Tracks fallback activations in HopeMeters.ModelFallbackActivations.
/// </summary>
internal sealed class FallbackChatProvider : IChatCompletionProvider
{
    private readonly IReadOnlyList<IChatCompletionProvider> _chain;
    private readonly ILogger<FallbackChatProvider> _log;

    /// <summary>
    /// Create a fallback chain. The first provider is primary; subsequent are fallbacks.
    /// </summary>
    public FallbackChatProvider(IEnumerable<IChatCompletionProvider> chain, ILogger<FallbackChatProvider> log)
    {
        _chain = chain.ToList();
        _log = log;

        if (_chain.Count == 0)
            throw new ArgumentException("Fallback chain must contain at least one provider");

        Name = $"fallback({string.Join("→", _chain.Select(p => p.Name))})";
    }

    public string Name { get; }

    public async Task<ChatResponse> CompleteAsync(ChatRequest request, CancellationToken ct)
    {
        for (int i = 0; i < _chain.Count; i++)
        {
            try
            {
                var result = await _chain[i].CompleteAsync(request, ct);

                if (i > 0)
                {
                    // Fallback was activated — emit metric
                    Hope.Agent.Application.Observability.HopeMeters.AgentRuns.Add(1,
                        new("type", "model_fallback"),
                        new("from", request.Model ?? "default"),
                        new("to", result.Model));
                    _log.LogWarning("Model fallback: primary failed → used {Provider} (attempt {Attempt}/{Total})",
                        result.Provider, i + 1, _chain.Count);
                }

                return result;
            }
            catch (Exception ex) when (ex is RateLimitExceededException or TimeoutException)
            {
                _log.LogWarning("Provider {Provider} unavailable (rate-limit/timeout): {Error}. Attempt {Attempt}/{Total}",
                    _chain[i].Name, ex.Message, i + 1, _chain.Count);
                // Continue to next fallback
            }
            catch (Exception ex) when (i == _chain.Count - 1)
            {
                // Last provider also failed with non-transient error → escalate
                _log.LogError(ex, "All {Total} fallback providers exhausted", _chain.Count);
                throw;
            }
        }

        throw new NoAvailableProviderException($"All {_chain.Count} providers exhausted — no model available");
    }

    public async IAsyncEnumerable<string> StreamAsync(ChatRequest request, [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken ct)
    {
        // Streaming: try each provider in order. The yield MUST NOT be inside
        // a try-catch block (CS1626), so we unwrap the enumeration manually.
        for (int i = 0; i < _chain.Count; i++)
        {
            var enumerator = _chain[i].StreamAsync(request, ct).GetAsyncEnumerator(ct);
            await using var _ = enumerator.ConfigureAwait(false);

            var reachedEnd = false;
            while (true)
            {
                string chunk;
                bool hasMore;
                try
                {
                    hasMore = await enumerator.MoveNextAsync();
                    if (!hasMore) { reachedEnd = true; break; }
                    chunk = enumerator.Current;
                }
                catch (Exception ex) when (ex is RateLimitExceededException or TimeoutException)
                {
                    _log.LogWarning("Streaming provider {Provider} unavailable — falling back. Attempt {Attempt}/{Total}",
                        _chain[i].Name, i + 1, _chain.Count);
                    break; // exit while → next provider
                }

                yield return chunk; // outside try-catch
            }

            if (reachedEnd)
            {
                if (i > 0)
                {
                    Hope.Agent.Application.Observability.HopeMeters.AgentRuns.Add(1,
                        new("type", "model_fallback"),
                        new("from", request.Model ?? "default"),
                        new("to", _chain[i].Name));
                }
                yield break; // success
            }
        }

        throw new NoAvailableProviderException($"All {_chain.Count} streaming providers exhausted");
    }
}

/// <summary>Exception thrown when no provider in the fallback chain is available.</summary>
public sealed class NoAvailableProviderException : Exception
{
    public NoAvailableProviderException(string message) : base(message) { }
}

/// <summary>Exception for rate-limit HTTP 429 responses from LLM providers.</summary>
public sealed class RateLimitExceededException : Exception
{
    public RateLimitExceededException(string provider, string? retryAfter = null)
        : base($"Provider '{provider}' rate-limited. Retry-After: {retryAfter ?? "unknown"}") { }
}

/// <summary>Exception for HTTP 408 or socket timeout from LLM providers.</summary>
public sealed class TimeoutException : Exception
{
    public TimeoutException(string provider, TimeSpan elapsed)
        : base($"Provider '{provider}' timed out after {elapsed.TotalSeconds:F1}s") { }
}
