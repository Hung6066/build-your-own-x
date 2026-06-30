namespace Hope.Agent.Application.Locking;

/// <summary>
/// Distributed lock abstraction for coordinating concurrent operations across
/// multiple API instances. Closes gap H-7. Used to prevent duplicate tool
/// executions when multiple instances receive the same request simultaneously.
/// </summary>
public interface IDistributedLock
{
    /// <summary>
    /// Attempt to acquire a distributed lock on the given resource key.
    /// Returns a disposable handle if acquired; null if the resource is already locked.
    /// </summary>
    /// <param name="resource">Unique resource key (e.g., "tool:PatientLookup:{userId}:{argsHash}").</param>
    /// <param name="expiry">Maximum time the lock will be held before auto-release.</param>
    /// <param name="ct">Cancellation token.</param>
    /// <returns>A disposable lock handle, or null if acquisition failed.</returns>
    Task<ILockHandle?> AcquireAsync(string resource, TimeSpan expiry, CancellationToken ct);
}

/// <summary>
/// A held distributed lock. Dispose to release the lock early;
/// otherwise it auto-expires after the configured TTL.
/// </summary>
public interface ILockHandle : IAsyncDisposable
{
    /// <summary>Unique token proving ownership of this lock.</summary>
    string Token { get; }

    /// <summary>The resource key that was locked.</summary>
    string Resource { get; }

    /// <summary>When this lock was acquired.</summary>
    DateTimeOffset AcquiredAt { get; }
}
