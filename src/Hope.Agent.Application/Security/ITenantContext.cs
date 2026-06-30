namespace Hope.Agent.Application.Security;

public interface ITenantContext
{
    Guid TenantId { get; }
    IDisposable Use(Guid tenantId);
}

public sealed class AsyncLocalTenantContext : ITenantContext
{
    private readonly AsyncLocal<Guid?> current = new();
    public Guid TenantId => current.Value ?? SecurityDefaults.DefaultTenantId;

    public IDisposable Use(Guid tenantId)
    {
        var previous = current.Value;
        current.Value = tenantId;
        return new Scope(this, previous);
    }

    private sealed class Scope(AsyncLocalTenantContext owner, Guid? previous) : IDisposable
    {
        public void Dispose() => owner.current.Value = previous;
    }
}
