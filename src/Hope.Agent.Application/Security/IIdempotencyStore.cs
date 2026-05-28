namespace Hope.Agent.Application.Security;

/// <summary>
/// Outcome of attempting to begin processing a request under an idempotency key.
/// Modeled after Stripe / Square / AWS idempotency semantics.
/// </summary>
public abstract record IdempotencyDecision
{
    /// <summary>No prior request seen for this key+user. Handler MUST run and call
    /// <see cref="IIdempotencyStore.CompleteAsync"/> once a response is produced.</summary>
    public sealed record Proceed : IdempotencyDecision;

    /// <summary>Another request with the same key is still executing. The caller
    /// should retry after a short delay (return 409 Conflict + Retry-After).</summary>
    public sealed record InProgress : IdempotencyDecision;

    /// <summary>A prior completed response exists for this key with a matching body
    /// hash. Replay it verbatim — never re-execute the handler.</summary>
    public sealed record Replay(int Status, byte[] Body) : IdempotencyDecision;

    /// <summary>The key has been used before, but with a different request body.
    /// Reject with 422 — clients must not reuse keys across distinct operations.</summary>
    public sealed record Mismatch : IdempotencyDecision;
}

/// <summary>
/// Server-side idempotency store. Implementations guarantee that, for a given
/// (userId, idempotencyKey) pair, only one handler invocation succeeds; concurrent
/// or retried requests receive either an in-progress signal or the cached response.
/// </summary>
public interface IIdempotencyStore
{
    /// <summary>
    /// Atomically reserves the (userId, key) slot.
    /// </summary>
    /// <param name="key">Caller-supplied idempotency key (max 255 chars).</param>
    /// <param name="userId">Authenticated user — keys are namespaced per user.</param>
    /// <param name="requestBodyHash">SHA-256 hex of the request body.</param>
    Task<IdempotencyDecision> TryBeginAsync(
        string key, Guid userId, string requestBodyHash, CancellationToken ct);

    /// <summary>
    /// Persists the final response so subsequent retries with the same key replay it.
    /// MUST be called exactly once after <see cref="TryBeginAsync"/> returned <c>Proceed</c>.
    /// </summary>
    Task CompleteAsync(
        string key, Guid userId, int status, string requestBodyHash, byte[] responseBody, CancellationToken ct);

    /// <summary>
    /// Releases an in-progress reservation without storing a response (used when
    /// the handler crashes, so subsequent retries are not blocked indefinitely).
    /// </summary>
    Task AbortAsync(string key, Guid userId, CancellationToken ct);
}
