using Hope.Agent.Application.Caching;
using Hope.Agent.Application.Observability;
using Hope.Agent.Application.Security;
using Hope.Agent.Application.Tools;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using System.Collections.Concurrent;
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
    IOptionsMonitor<RuntimeSandboxOptions> sandboxOptions,
    IPromptShield outputRail,
    IToolResultCache toolCache,
    ILogger<SandboxedToolExecutor> log) : IToolExecutor
{
    private static readonly ConcurrentDictionary<string, SemaphoreSlim> ToolSemaphores = new(StringComparer.OrdinalIgnoreCase);

    public async Task<string> InvokeAsync(IAgentTool tool, string argumentsJson, ToolInvocationContext context, CancellationToken ct)
    {
        var currentOptions = opts.CurrentValue;
        var sandbox = sandboxOptions.CurrentValue;
        var maxArgBytes = Math.Max(1_024, currentOptions.SandboxMaxArgumentsBytes);
        var maxOutputBytes = Math.Max(4_096, currentOptions.SandboxMaxOutputBytes);
        var impact = currentOptions.Tools.TryGetValue(tool.Definition.Name, out var configuredImpact)
            ? configuredImpact
            : currentOptions.DefaultImpact;

        if (sandbox.KillSwitch.TryGetValue(tool.Definition.Name, out var disabled) && disabled)
        {
            HopeMeters.BlockedToolCalls.Add(1, new("tool", tool.Definition.Name), new("reason", "kill_switch"));
            throw new InvalidOperationException($"Tool '{tool.Definition.Name}' is disabled by runtime sandbox kill switch.");
        }

        if (currentOptions.RequireIdempotencyKeyForWrites
            && impact is Hope.Agent.Domain.Security.ToolImpactLevel.Write or Hope.Agent.Domain.Security.ToolImpactLevel.Critical
            && string.IsNullOrWhiteSpace(context.IdempotencyKey))
        {
            HopeMeters.ToolErrors.Add(1,
                new KeyValuePair<string, object?>("tool", tool.Definition.Name),
                new KeyValuePair<string, object?>("reason", "missing_idempotency_key"));
            throw new InvalidOperationException($"Tool '{tool.Definition.Name}' requires an idempotency key for write/critical invocation.");
        }

        if (sandbox.RequireIsolationForWriteTools
            && impact is Hope.Agent.Domain.Security.ToolImpactLevel.Write or Hope.Agent.Domain.Security.ToolImpactLevel.Critical
            && string.Equals(sandbox.Mode, "in-process", StringComparison.OrdinalIgnoreCase))
        {
            HopeMeters.BlockedToolCalls.Add(1, new("tool", tool.Definition.Name), new("reason", "sandbox_isolation_required"));
            throw new InvalidOperationException($"Tool '{tool.Definition.Name}' requires isolated-process/container sandbox execution.");
        }

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

            ValidateSchema(tool, doc.RootElement);
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

        var concurrencyLimit = currentOptions.PerToolConcurrencyLimit.TryGetValue(tool.Definition.Name, out var configuredLimit)
            ? configuredLimit
            : currentOptions.DefaultPerToolConcurrencyLimit;
        concurrencyLimit = Math.Clamp(concurrencyLimit, 1, 10_000);
        var semaphore = ToolSemaphores.GetOrAdd(tool.Definition.Name, _ => new SemaphoreSlim(concurrencyLimit, concurrencyLimit));
        if (!await semaphore.WaitAsync(TimeSpan.FromMilliseconds(250), cts.Token).ConfigureAwait(false))
        {
            HopeMeters.ToolErrors.Add(1,
                new KeyValuePair<string, object?>("tool", tool.Definition.Name),
                new KeyValuePair<string, object?>("reason", "tool_rate_limited"));
            throw new InvalidOperationException($"Tool '{tool.Definition.Name}' is currently rate limited by the tool gateway.");
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
        finally
        {
            semaphore.Release();
        }
    }

    private static void ValidateSchema(IAgentTool tool, JsonElement args)
    {
        try
        {
            using var schema = JsonDocument.Parse(tool.Definition.ParametersJsonSchema);
            if (!schema.RootElement.TryGetProperty("required", out var required)
                || required.ValueKind != JsonValueKind.Array)
            {
                return;
            }

            var missing = required.EnumerateArray()
                .Select(x => x.GetString())
                .Where(x => !string.IsNullOrWhiteSpace(x))
                .Where(name => !args.TryGetProperty(name!, out var value)
                    || value.ValueKind is JsonValueKind.Null or JsonValueKind.Undefined
                    || (value.ValueKind == JsonValueKind.String && string.IsNullOrWhiteSpace(value.GetString())))
                .ToArray();

            if (missing.Length > 0)
            {
                throw new ArgumentException(
                    $"Tool '{tool.Definition.Name}' missing required argument(s): {string.Join(", ", missing)}.");
            }

            if (schema.RootElement.TryGetProperty("additionalProperties", out var additional)
                && additional.ValueKind == JsonValueKind.False
                && schema.RootElement.TryGetProperty("properties", out var strictProps)
                && strictProps.ValueKind == JsonValueKind.Object)
            {
                var allowed = strictProps.EnumerateObject().Select(p => p.Name).ToHashSet(StringComparer.Ordinal);
                var unknown = args.EnumerateObject().Select(p => p.Name).Where(name => !allowed.Contains(name)).ToArray();
                if (unknown.Length > 0)
                    throw new ArgumentException($"Tool '{tool.Definition.Name}' unknown argument(s): {string.Join(", ", unknown)}.");
            }

            if (!schema.RootElement.TryGetProperty("properties", out var properties)
                || properties.ValueKind != JsonValueKind.Object)
            {
                return;
            }

            foreach (var prop in properties.EnumerateObject())
            {
                if (!args.TryGetProperty(prop.Name, out var value) || value.ValueKind is JsonValueKind.Null or JsonValueKind.Undefined)
                    continue;

                if (prop.Value.TryGetProperty("enum", out var enumValues) && enumValues.ValueKind == JsonValueKind.Array)
                {
                    var actual = value.ValueKind == JsonValueKind.String ? value.GetString() : value.GetRawText();
                    var allowed = enumValues.EnumerateArray()
                        .Select(x => x.ValueKind == JsonValueKind.String ? x.GetString() : x.GetRawText())
                        .ToHashSet(StringComparer.OrdinalIgnoreCase);
                    if (actual is not null && !allowed.Contains(actual))
                        throw new ArgumentException($"Tool '{tool.Definition.Name}' argument '{prop.Name}' is not one of the allowed enum values.");
                }

                if (!prop.Value.TryGetProperty("type", out var typeElement))
                    continue;

                var expectedTypes = typeElement.ValueKind == JsonValueKind.Array
                    ? typeElement.EnumerateArray().Select(x => x.GetString()).Where(x => !string.IsNullOrWhiteSpace(x)).ToArray()
                    : [typeElement.GetString()];
                if (!expectedTypes.Any(t => MatchesJsonSchemaType(value, t)))
                    throw new ArgumentException($"Tool '{tool.Definition.Name}' argument '{prop.Name}' has invalid type '{value.ValueKind}'.");
            }
        }
        catch (JsonException)
        {
            // A malformed schema is a tool authoring problem. Do not block runtime use;
            // individual tools still validate by reading their required arguments.
        }
    }

    private static bool MatchesJsonSchemaType(JsonElement value, string? expected)
    {
        return expected switch
        {
            "string" => value.ValueKind == JsonValueKind.String,
            "number" => value.ValueKind == JsonValueKind.Number,
            "integer" => value.ValueKind == JsonValueKind.Number && value.TryGetInt64(out _),
            "boolean" => value.ValueKind is JsonValueKind.True or JsonValueKind.False,
            "object" => value.ValueKind == JsonValueKind.Object,
            "array" => value.ValueKind == JsonValueKind.Array,
            "null" => value.ValueKind == JsonValueKind.Null,
            _ => true,
        };
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
