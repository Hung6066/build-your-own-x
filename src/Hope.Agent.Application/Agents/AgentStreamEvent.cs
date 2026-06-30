using System.Text.Json.Serialization;

namespace Hope.Agent.Application.Agents;

/// <summary>
/// Polymorphic streaming event envelope for Server-Sent Events (SSE).
/// Closes gap C-6. Replaces raw string chunks with typed events so clients
/// can distinguish tokens, tool call lifecycle, plan updates, errors, and
/// completion — enabling rich UX (progress bars, tool call spinners, etc.).
///
/// SSE format (OpenAI-compatible):
///   data: {"type":"token","text":"Xin","index":0}
///   data: {"type":"tool_call_start","toolCallId":"...","toolName":"...","arguments":"..."}
///   data: {"type":"finish","finishReason":"stop","usage":{...},"costUsd":0.000345}
/// </summary>
[JsonPolymorphic(TypeDiscriminatorPropertyName = "type")]
[JsonDerivedType(typeof(TokenEvent), "token")]
[JsonDerivedType(typeof(ToolCallStartEvent), "tool_call_start")]
[JsonDerivedType(typeof(ToolCallEndEvent), "tool_call_end")]
[JsonDerivedType(typeof(PlanUpdateEvent), "plan_update")]
[JsonDerivedType(typeof(ErrorEvent), "error")]
[JsonDerivedType(typeof(FinishEvent), "finish")]
public abstract record AgentStreamEvent
{
    /// <summary>Monotonic sequence number for client-side ordering.</summary>
    public long Sequence { get; init; }

    /// <summary>ISO 8601 timestamp of event generation.</summary>
    public DateTimeOffset Timestamp { get; init; } = DateTimeOffset.UtcNow;
}

public sealed record TokenEvent(
    string Text,
    int Index
) : AgentStreamEvent;

public sealed record ToolCallStartEvent(
    string ToolCallId,
    string ToolName,
    string Arguments
) : AgentStreamEvent;

public sealed record ToolCallEndEvent(
    string ToolCallId,
    string ToolName,
    string Result,
    bool Success,
    TimeSpan Duration
) : AgentStreamEvent;

public sealed record PlanUpdateEvent(
    string Step,
    string Status,
    string? Detail
) : AgentStreamEvent;

public sealed record ErrorEvent(
    string Code,
    string Message
) : AgentStreamEvent;

public sealed record FinishEvent(
    string FinishReason,
    TokenUsage Usage,
    decimal CostUsd
) : AgentStreamEvent;

/// <summary>Token usage summary attached to FinishEvent.</summary>
public sealed record TokenUsage(
    int PromptTokens,
    int CompletionTokens,
    int TotalTokens);
