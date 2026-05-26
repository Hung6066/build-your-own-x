using System.Text;
using Hope.Agent.Application.Agents.Multi;
using Hope.Agent.Application.Agents.ReAct;
using Hope.Agent.Application.Learning;
using Hope.Agent.Application.LLM;
using Hope.Agent.Application.Tools;
using Microsoft.Extensions.Logging;

namespace Hope.Agent.MultiAgent.ReAct;

/// <summary>
/// ReAct (Reasoning + Acting) loop implementation (Yao et al., 2022).
/// <para>
/// Each iteration:
/// <list type="number">
///   <item>LLM emits: <c>Thought: …</c> / <c>Action: tool_name</c> / <c>Action Input: {…}</c></item>
///   <item>The loop invokes the named tool and appends the <c>Observation</c>.</item>
///   <item>Repeat until the LLM emits <c>Final Answer:</c> or the iteration budget is exhausted.</item>
/// </list>
/// </para>
/// Optional <see cref="IReflector"/> performs a Constitutional-AI style critique
/// and refinement of the final answer when <see cref="ReActOptions.EnableReflection"/> is set.
/// </summary>
internal sealed class ReActLoop(
    ILLMRouter llm,
    ILogger<ReActLoop> log,
    IReflector? reflector = null) : IReActLoop
{
    private const string BaseSystemPrompt = """
        You are a clinical operations AI. Solve the task using the available tools step by step.

        To call a tool, respond EXACTLY in this format:
        Thought: <your reasoning>
        Action: <tool_name>
        Action Input: <valid JSON matching the tool's parameter schema>

        When you have a complete answer:
        Thought: <final reasoning>
        Final Answer: <answer — JSON or plain text>

        Rules:
        - Always start your response with "Thought:"
        - Only use tools listed in "Available Tools" below
        - Never fabricate tool outputs; wait for the Observation
        - If a tool returns an error, adapt your approach
        - PHI: never echo unnecessary patient identifiers in your reasoning
        """;

    public async Task<ReActResult> RunAsync(
        AgentTask task,
        IReadOnlyList<IAgentTool> availableTools,
        ReActOptions? options = null,
        CancellationToken ct = default)
    {
        var opts = options ?? new ReActOptions();
        var toolMap = availableTools.ToDictionary(t => t.Definition.Name, StringComparer.OrdinalIgnoreCase);
        var toolDescriptions = BuildToolDescriptions(availableTools);

        var systemContent = new StringBuilder(BaseSystemPrompt)
            .Append("\n\nAvailable Tools:\n")
            .Append(toolDescriptions);
        if (opts.SystemPromptSuffix is { Length: > 0 } suffix)
            systemContent.Append("\n\n").Append(suffix);

        var contextSnippet = task.Context.Count > 0
            ? "\nContext: " + System.Text.Json.JsonSerializer.Serialize(task.Context)
            : string.Empty;

        var messages = new List<ChatMessage>
        {
            new("system", systemContent.ToString()),
            new("user", $"Task: {task.Input}{contextSnippet}"),
        };

        var ctx = new ToolInvocationContext(
            task.UserId,
            task.ConversationId ?? Guid.Empty,
            task.CorrelationId ?? string.Empty);

        var chat = llm.SelectChat();
        var steps = new List<ReActStep>();

        log.LogDebug("ReAct starting for task {Id}, intent={Intent}, tools={Count}",
            task.TaskId, task.Intent, availableTools.Count);

        for (int iter = 0; iter < opts.MaxIterations; iter++)
        {
            var resp = await chat.CompleteAsync(
                new ChatRequest(messages, Temperature: opts.Temperature), ct);
            var text = resp.Content.Trim();

            var thought = ExtractSection(text, "Thought:", ["Action:", "Final Answer:"]);
            var finalAnswer = ExtractSection(text, "Final Answer:", []);

            // --- Final Answer ---
            if (!string.IsNullOrEmpty(finalAnswer))
            {
                steps.Add(new ReActStep(iter, thought, "Final Answer", finalAnswer, null, true));
                messages.Add(new("assistant", text));

                string? critique = null;
                if (opts.EnableReflection && reflector is not null)
                {
                    try
                    {
                        var reflection = await reflector.CritiqueAndRefineAsync(task.Input, finalAnswer, ct);
                        log.LogDebug("ReAct reflection score={Score:F2}", reflection.Score);
                        finalAnswer = reflection.RefinedAnswer;
                        critique = reflection.Critique;
                    }
                    catch (Exception ex)
                    {
                        log.LogWarning(ex, "Reflection step failed; using unrefined answer");
                    }
                }

                log.LogDebug("ReAct finished in {Iter} iterations", iter + 1);
                return new ReActResult(true, finalAnswer, steps, critique);
            }

            // --- Action ---
            var actionName = ExtractSection(text, "Action:", ["Action Input:", "Observation:"]).Trim();
            var actionInput = ExtractSection(text, "Action Input:", ["Observation:", "Thought:"]).Trim();

            if (string.IsNullOrEmpty(actionName))
            {
                // LLM did not follow the format — treat full response as the answer
                log.LogWarning("ReAct iter={Iter}: LLM did not produce Action or Final Answer; treating as final", iter);
                steps.Add(new ReActStep(iter, thought, "Final Answer", text, null, true));
                return new ReActResult(true, text, steps);
            }

            // Invoke tool
            string observation;
            if (toolMap.TryGetValue(actionName, out var tool))
            {
                try
                {
                    observation = await tool.InvokeAsync(actionInput, ctx, ct);
                    log.LogDebug("ReAct iter={Iter}: tool={Tool} → {ObsLen} chars", iter, actionName, observation.Length);
                }
                catch (Exception ex)
                {
                    observation = $"Tool error: {ex.Message}";
                    log.LogWarning(ex, "ReAct iter={Iter}: tool={Tool} threw", iter, actionName);
                }
            }
            else
            {
                observation = $"Unknown tool '{actionName}'. Available: {string.Join(", ", toolMap.Keys)}";
                log.LogWarning("ReAct iter={Iter}: unknown tool '{Tool}'", iter, actionName);
            }

            steps.Add(new ReActStep(iter, thought, actionName, actionInput, observation, false));
            messages.Add(new("assistant", text));
            messages.Add(new("user", $"Observation: {observation}"));
        }

        // Budget exhausted — return the last observation as a partial answer
        var lastStep = steps.Count > 0 ? steps[^1] : null;
        var partialAnswer = lastStep?.Observation ?? lastStep?.ActionInput ?? "(no output)";
        log.LogWarning("ReAct exhausted {Max} iterations for task {Id}", opts.MaxIterations, task.TaskId);
        return new ReActResult(false, partialAnswer, steps);
    }

    private static string BuildToolDescriptions(IReadOnlyList<IAgentTool> tools) =>
        string.Join("\n\n", tools.Select(t =>
            $"Tool: {t.Definition.Name}\nDescription: {t.Definition.Description}\nParameters: {t.Definition.ParametersJsonSchema}"));

    /// <summary>
    /// Extracts the text between a section header and the first occurrence of any stop header.
    /// Returns empty string if the header is not found.
    /// </summary>
    private static string ExtractSection(string text, string header, string[] stopHeaders)
    {
        var start = text.IndexOf(header, StringComparison.OrdinalIgnoreCase);
        if (start < 0) return string.Empty;
        start += header.Length;
        var end = text.Length;
        foreach (var stop in stopHeaders)
        {
            var idx = text.IndexOf(stop, start, StringComparison.OrdinalIgnoreCase);
            if (idx > start && idx < end) end = idx;
        }
        return text[start..end].Trim();
    }
}
