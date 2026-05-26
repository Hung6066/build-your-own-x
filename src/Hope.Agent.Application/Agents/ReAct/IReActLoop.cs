using Hope.Agent.Application.Agents.Multi;
using Hope.Agent.Application.Tools;

namespace Hope.Agent.Application.Agents.ReAct;

/// <summary>
/// ReAct (Reasoning + Acting) loop contract.
/// Enables an agent role to reason iteratively — think → call a tool → observe output →
/// think again — until it arrives at a Final Answer or exhausts the iteration budget.
/// </summary>
public interface IReActLoop
{
    /// <param name="task">The agent task to solve.</param>
    /// <param name="availableTools">Tools the loop may invoke.</param>
    /// <param name="options">Tuning options. Uses safe defaults when null.</param>
    Task<ReActResult> RunAsync(
        AgentTask task,
        IReadOnlyList<IAgentTool> availableTools,
        ReActOptions? options = null,
        CancellationToken ct = default);
}

/// <summary>Tuning knobs for a single ReAct invocation.</summary>
public sealed class ReActOptions
{
    /// <summary>Maximum think → act → observe iterations before giving up.</summary>
    public int MaxIterations { get; init; } = 5;

    public float Temperature { get; init; } = 0.1f;

    /// <summary>
    /// When <c>true</c>, applies <see cref="IReflector"/> critique to the final answer
    /// if a reflector is available in the concrete implementation.
    /// </summary>
    public bool EnableReflection { get; init; }

    /// <summary>Extra instructions appended to the base system prompt.</summary>
    public string? SystemPromptSuffix { get; init; }
}

/// <summary>Full result of one ReAct execution.</summary>
public sealed record ReActResult(
    bool Success,
    string FinalAnswer,
    IReadOnlyList<ReActStep> Steps,
    string? ReflectionCritique = null);

/// <summary>One think → act → observe iteration.</summary>
public sealed record ReActStep(
    int Iteration,
    string Thought,
    /// <summary>Tool name, or "Final Answer".</summary>
    string ActionName,
    /// <summary>JSON arguments sent to the tool, or the final answer text.</summary>
    string ActionInput,
    /// <summary>Tool output. <c>null</c> for the Final Answer step.</summary>
    string? Observation,
    bool IsFinal);
