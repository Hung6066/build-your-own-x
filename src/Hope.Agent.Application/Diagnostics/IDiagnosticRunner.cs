namespace Hope.Agent.Application.Diagnostics;

public sealed record HealthCheckResult(string Name, bool Healthy, string Message, TimeSpan Duration);

public sealed record DiagnosticReport(
    DateTimeOffset GeneratedAt,
    bool AllHealthy,
    IReadOnlyList<HealthCheckResult> Checks);

public interface IDiagnosticRunner
{
    Task<DiagnosticReport> RunAsync(CancellationToken ct);
}
