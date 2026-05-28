using Hope.Agent.Application.Caching;
using Hope.Agent.Application.Observability;
using Hope.Agent.Application.Security;
using Hope.Agent.Application.Tools;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using System.Security.Cryptography;
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
    IToolResultCache toolCache,
    ILogger<SandboxedToolExecutor> log)
{
    public async Task<string> InvokeAsync(IAgentTool tool, string argumentsJson, ToolInvocationContext context, CancellationToken ct)
    {
        var currentOptions = opts.CurrentValue;
        var maxArgBytes = Math.Max(1_024, currentOptions.SandboxMaxArgumentsBytes);
        var maxOutputBytes = Math.Max(4_096, currentOptions.SandboxMaxOutputBytes);

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

        var argsBytes = System.Text.Encoding.UTF8.GetByteCount(normalizedArgs);
        if (argsBytes > maxArgBytes)
        {
            log.LogWarning(
                "Sandboxed tool {Tool}: arguments size {Bytes} exceeds limit {Limit}. Invocation rejected.",
                tool.Definition.Name, argsBytes, maxArgBytes);
            HopeMeters.ToolErrors.Add(1,
                new KeyValuePair<string, object?>("tool", tool.Definition.Name),
                new KeyValuePair<string, object?>("reason", "arguments_too_large"));
            throw new ArgumentException(
                $"Tool '{tool.Definition.Name}': argumentsJson exceeds sandbox limit of {maxArgBytes} bytes.");
        }

        // ── LLM04: hard wall-clock timeout ────────────────────────────────────
        var timeoutMs = Math.Clamp(currentOptions.SandboxToolTimeoutMs, 1_000, 120_000);
        using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        cts.CancelAfter(TimeSpan.FromMilliseconds(timeoutMs));

        // ── Tool result cache (opt-in per tool via IAgentTool.IsCacheable) ────
        string? argsHash = null;
        if (tool.IsCacheable)
        {
            argsHash = Convert.ToHexString(SHA256.HashData(System.Text.Encoding.UTF8.GetBytes(normalizedArgs)));
            var cached = await toolCache.LookupAsync(tool.Definition.Name, argsHash, (Guid?)context.UserId, cts.Token).ConfigureAwait(false);
            if (cached is not null)
            {
                HopeMeters.ToolCacheHits.Add(1, new KeyValuePair<string, object?>("tool", tool.Definition.Name));
                return cached;
            }
        }

        try
        {
            var output = await tool.InvokeAsync(normalizedArgs, context, cts.Token).ConfigureAwait(false);

            var outputBytes = System.Text.Encoding.UTF8.GetByteCount(output);
            if (outputBytes > maxOutputBytes)
            {
                log.LogWarning(
                    "Sandboxed tool {Tool} output size {Bytes} exceeds limit {Limit}. Truncated.",
                    tool.Definition.Name, outputBytes, maxOutputBytes);
                HopeMeters.ToolErrors.Add(1,
                    new KeyValuePair<string, object?>("tool", tool.Definition.Name),
                    new KeyValuePair<string, object?>("reason", "output_truncated"));
                output = TruncateUtf8(output, maxOutputBytes);
            }

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

            if (tool.IsCacheable && argsHash is not null)
            {
                await toolCache.StoreAsync(tool.Definition.Name, argsHash, (Guid?)context.UserId, output, tool.CacheTtl, cts.Token).ConfigureAwait(false);
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

    private static string TruncateUtf8(string value, int maxBytes)
    {
        if (System.Text.Encoding.UTF8.GetByteCount(value) <= maxBytes)
            return value;

        var length = value.Length;
        while (length > 0)
        {
            length -= Math.Max(1, length / 8);
            var candidate = value[..length];
            if (System.Text.Encoding.UTF8.GetByteCount(candidate) <= maxBytes)
                return candidate;
        }

        return string.Empty;
    }
}
