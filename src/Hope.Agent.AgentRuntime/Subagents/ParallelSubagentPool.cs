using System.Diagnostics;
using System.Text;
using Hope.Agent.Application.Agents;
using Hope.Agent.Application.LLM;
using Hope.Agent.Application.Observability;
using Hope.Agent.Application.Subagents;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace Hope.Agent.AgentRuntime.Subagents;

internal sealed class ParallelSubagentPool(
    IServiceScopeFactory scopes,
    ILLMRouter llm,
    IOptions<SubagentPoolOptions> opts,
    ILogger<ParallelSubagentPool> log) : ISubagentPool
{
    public async Task<SubagentAggregateResult> FanOutAsync(SubagentRequest request, CancellationToken ct)
    {
        var o = opts.Value;
        var sw = Stopwatch.StartNew();
        if (!o.Enabled || request.Specs.Count == 0)
        {
            return new SubagentAggregateResult(string.Empty, Array.Empty<SubagentBranchResult>(), sw.Elapsed);
        }

        using var gate = new SemaphoreSlim(Math.Max(1, o.MaxParallelism));
        var tasks = request.Specs.Select(spec => RunBranchAsync(spec, request, gate, o.PerBranchTimeoutSeconds, ct)).ToList();
        var branches = await Task.WhenAll(tasks);

        var ok = branches.Where(b => b.Error is null && !string.IsNullOrWhiteSpace(b.Reply)).ToList();
        string aggregate;
        if (ok.Count == 0)
        {
            aggregate = "All sub-agents failed to produce a response.";
        }
        else
        {
            var sb = new StringBuilder();
            foreach (var b in ok)
                sb.Append("## ").Append(b.Profile).Append('\n').Append(b.Reply).Append("\n\n");

            var aggReq = new ChatRequest(
                Messages:
                [
                    new ChatMessage("system", o.AggregationPrompt),
                    new ChatMessage("user", $"Original question:\n{request.Question}\n\nBranch responses:\n{sb}"),
                ],
                Temperature: 0.2f,
                MaxTokens: 800);
            try
            {
                var resp = await llm.SelectChat().CompleteAsync(aggReq, ct);
                aggregate = resp.Content;
            }
            catch (Exception ex)
            {
                log.LogWarning(ex, "Subagent aggregation failed; returning concatenated branches.");
                aggregate = sb.ToString();
            }
        }

        HopeMeters.SubagentFanOuts.Add(1,
            new KeyValuePair<string, object?>("branches", branches.Length),
            new KeyValuePair<string, object?>("failed", branches.Count(b => b.Error is not null)));
        return new SubagentAggregateResult(aggregate, branches, sw.Elapsed);
    }

    private async Task<SubagentBranchResult> RunBranchAsync(
        SubagentSpec spec,
        SubagentRequest request,
        SemaphoreSlim gate,
        int timeoutSeconds,
        CancellationToken ct)
    {
        await gate.WaitAsync(ct);
        var sw = Stopwatch.StartNew();
        try
        {
            await using var scope = scopes.CreateAsyncScope();
            var runtime = scope.ServiceProvider.GetRequiredService<IAgentRuntime>();
            using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
            cts.CancelAfter(TimeSpan.FromSeconds(timeoutSeconds));
            var message = string.IsNullOrWhiteSpace(spec.SystemPromptHint)
                ? request.Question
                : $"[Specialist hint: {spec.SystemPromptHint}]\n\n{request.Question}";
            var resp = await runtime.RunAsync(
                new AgentRequest(request.UserId, request.ParentConversationId, message, spec.Profile, request.CorrelationId),
                cts.Token);
            return new SubagentBranchResult(spec.Profile, resp.Reply, resp.PromptTokens, resp.CompletionTokens, sw.Elapsed);
        }
        catch (Exception ex)
        {
            log.LogWarning(ex, "Subagent branch {Profile} failed.", spec.Profile);
            return new SubagentBranchResult(spec.Profile, string.Empty, 0, 0, sw.Elapsed, ex.Message);
        }
        finally
        {
            gate.Release();
        }
    }
}
