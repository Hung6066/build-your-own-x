using Hope.Agent.Application.Observability;
using Hope.Agent.Application.Security;
using Hope.Agent.Application.Tools;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace Hope.Agent.AgentRuntime.Security;

/// <summary>
/// Wraps an <see cref="IAgentTool"/> invocation with a hard wall-clock timeout.
/// First-cut sandbox: enforces timeout + structured logging. File/network isolation
/// is left to per-tool implementations (clients/HttpClients) and future hardening.
/// </summary>
public sealed class SandboxedToolExecutor(
    IOptionsMonitor<ToolApprovalOptions> opts,
    ILogger<SandboxedToolExecutor> log)
{
    public async Task<string> InvokeAsync(IAgentTool tool, string argumentsJson, ToolInvocationContext context, CancellationToken ct)
    {
        var timeoutMs = Math.Max(1_000, opts.CurrentValue.SandboxToolTimeoutMs);
        using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        cts.CancelAfter(TimeSpan.FromMilliseconds(timeoutMs));

        try
        {
            return await tool.InvokeAsync(argumentsJson, context, cts.Token).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested)
        {
            log.LogWarning("Sandboxed tool {Tool} exceeded timeout {TimeoutMs}ms", tool.Definition.Name, timeoutMs);
            HopeMeters.ToolErrors.Add(1,
                new KeyValuePair<string, object?>("tool", tool.Definition.Name),
                new KeyValuePair<string, object?>("reason", "sandbox_timeout"));
            throw new TimeoutException($"Tool '{tool.Definition.Name}' exceeded sandbox timeout of {timeoutMs}ms");
        }
    }
}
