using Hope.Agent.Application.Observability;
using Hope.Agent.Application.Security;
using Hope.Agent.Application.Tools;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using System.Text.Json;

namespace Hope.Agent.AgentRuntime.Security;

/// <summary>
/// Wraps an <see cref="IAgentTool"/> invocation with enterprise-grade boundary enforcement:
/// <list type="bullet">
///   <item>Validates <c>argumentsJson</c> is a well-formed JSON object (LLM07 — Insecure Plugin Design).</item>
///   <item>Enforces a hard wall-clock timeout (LLM04 — DoS / resource exhaustion).</item>
///   <item>Screens tool output for prompt-injection patterns before the result is fed back to the LLM
///         (NeMo Guardrails execution rail — indirect injection via compromised MCP tools).</item>
/// </list>
/// File/network isolation is left to per-tool HttpClient scoping and future host-level hardening.
/// </summary>
public sealed class SandboxedToolExecutor(
    IOptionsMonitor<ToolApprovalOptions> opts,
    IPromptShield outputRail,
    ILogger<SandboxedToolExecutor> log)
{
    public async Task<string> InvokeAsync(IAgentTool tool, string argumentsJson, ToolInvocationContext context, CancellationToken ct)
    {
        // ── LLM07: validate args are a well-formed JSON object ────────────────
        // Prevents malformed / crafted payloads from crashing tool implementations
        // or exploiting unchecked deserialization.
        var normalizedArgs = string.IsNullOrWhiteSpace(argumentsJson) ? "{}" : argumentsJson.Trim();
        try
        {
            using var doc = JsonDocument.Parse(normalizedArgs);
            if (doc.RootElement.ValueKind != JsonValueKind.Object)
            {
                log.LogWarning(
                    "Sandboxed tool {Tool}: arguments are not a JSON object (got {Kind}). Invocation rejected.",
                    tool.Definition.Name, doc.RootElement.ValueKind);
                HopeMeters.ToolErrors.Add(1,
                    new KeyValuePair<string, object?>("tool", tool.Definition.Name),
                    new KeyValuePair<string, object?>("reason", "invalid_arg_type"));
                throw new ArgumentException(
                    $"Tool '{tool.Definition.Name}': argumentsJson must be a JSON object, got {doc.RootElement.ValueKind}.");
            }
        }
        catch (JsonException ex)
        {
            log.LogWarning("Sandboxed tool {Tool}: malformed JSON arguments — {Error}", tool.Definition.Name, ex.Message);
            HopeMeters.ToolErrors.Add(1,
                new KeyValuePair<string, object?>("tool", tool.Definition.Name),
                new KeyValuePair<string, object?>("reason", "malformed_json_args"));
            throw new ArgumentException($"Tool '{tool.Definition.Name}': malformed JSON arguments.", ex);
        }

        // ── LLM04: hard wall-clock timeout ────────────────────────────────────
        var timeoutMs = Math.Max(1_000, opts.CurrentValue.SandboxToolTimeoutMs);
        using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        cts.CancelAfter(TimeSpan.FromMilliseconds(timeoutMs));

        try
        {
            var output = await tool.InvokeAsync(normalizedArgs, context, cts.Token).ConfigureAwait(false);

            // ── NeMo Guardrails execution rail: screen tool output for prompt injection ──
            // A compromised MCP tool could return "Ignore previous instructions..." in its
            // output, which then gets injected back into the LLM's context as a tool message.
            var outputCheck = outputRail.Inspect(output);
            if (!outputCheck.Allowed)
            {
                log.LogWarning(
                    "ExecutionRail: tool '{Tool}' output contains injection pattern [{Reasons}]. Sanitized before returning to LLM.",
                    tool.Definition.Name, string.Join(", ", outputCheck.Reasons));
                HopeMeters.ToolErrors.Add(1,
                    new KeyValuePair<string, object?>("tool", tool.Definition.Name),
                    new KeyValuePair<string, object?>("reason", "output_injection"));
                return outputCheck.SanitizedInput;
            }

            return output;
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
