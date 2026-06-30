using System.Text.Json;
using Hope.Agent.Application.LLM;
using Hope.Agent.Application.Learning;
using Hope.Agent.Application.Prompts;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace Hope.Agent.Infrastructure.Prompts;

internal sealed class PromptOptimizationService(
    IPromptRegistry registry,
    IEvalCaseStore cases,
    IJudge judge,
    ILLMRouter router,
    IOptionsMonitor<PromptOptimizationOptions> options,
    ILogger<PromptOptimizationService> log) : IPromptOptimizationService
{
    public async Task<PromptOptimizationResult> OptimizeAsync(string promptName, string suite, bool? autoPromote, CancellationToken ct)
    {
        var active = await registry.GetAsync(promptName, null, ct).ConfigureAwait(false);
        var baseline = await ScoreAsync(active.Content, suite, ct).ConfigureAwait(false);
        var candidates = GenerateCandidates(active.Content, options.CurrentValue.CandidateCount)
            .Select(content => new PromptTemplate(
                Guid.CreateVersion7(),
                promptName,
                VersionFromContent(content),
                content,
                ["optimized", suite],
                active.TenantId,
                DateTimeOffset.UtcNow,
                "prompt-optimizer",
                false))
            .ToList();

        var scores = new List<PromptCandidateScore>();
        PromptTemplate best = active;
        var bestScore = baseline.Score;
        foreach (var candidate in candidates)
        {
            await registry.RegisterAsync(candidate, ct).ConfigureAwait(false);
            var score = await ScoreAsync(candidate.Content, suite, ct).ConfigureAwait(false);
            scores.Add(new PromptCandidateScore(candidate.Version, score.Score, score.Passed, score.Reason));
            if (score.Score > bestScore)
            {
                best = candidate;
                bestScore = score.Score;
            }
        }

        var promote = (autoPromote ?? options.CurrentValue.AutoPromote)
            && best.Version != active.Version
            && bestScore >= baseline.Score + options.CurrentValue.MinPromotionDelta;
        if (promote)
        {
            await registry.ActivateVersionAsync(promptName, best.Version, ct).ConfigureAwait(false);
            log.LogInformation("Prompt optimizer promoted {Prompt} {Version} score={Score:F3}", promptName, best.Version[..8], bestScore);
        }

        return new PromptOptimizationResult(
            promptName,
            suite,
            active.Version,
            baseline.Score,
            best.Version,
            bestScore,
            promote,
            scores);
    }

    private async Task<(double Score, bool Passed, string Reason)> ScoreAsync(string systemPrompt, string suite, CancellationToken ct)
    {
        var evalCases = await cases.GetBySuiteAsync(suite, ct).ConfigureAwait(false);
        if (evalCases.Count == 0)
            return (0, false, "no eval cases");

        var chat = router.SelectChat();
        double total = 0;
        var passed = 0;
        var reasons = new List<string>();
        foreach (var item in evalCases)
        {
            var resp = await chat.CompleteAsync(new ChatRequest(
                Messages: [new("system", systemPrompt), new("user", item.UserMessage)],
                Temperature: 0.0f,
                MaxTokens: 512), ct).ConfigureAwait(false);
            var verdict = await judge.ScoreAsync(item.UserMessage, resp.Content, item.ReferenceAnswer, ct).ConfigureAwait(false);
            total += verdict.Score;
            if (verdict.Passed) passed++;
            reasons.Add($"{item.Name}:{verdict.Score:F2}");
        }
        var avg = total / evalCases.Count;
        return (avg, passed == evalCases.Count, JsonSerializer.Serialize(reasons));
    }

    private static IReadOnlyList<string> GenerateCandidates(string baseline, int count)
    {
        var guards = new[]
        {
            "\n\nUse only evidence present in retrieved context. State uncertainty explicitly.",
            "\n\nBefore finalizing, check for unsupported clinical claims and remove them.",
            "\n\nPrefer concise answers with source-grounded facts, no invented citations, and no PHI echo.",
            "\n\nFor tool use, call only tools allowed for the current role and explain denials briefly.",
        };
        return guards.Take(Math.Clamp(count, 1, guards.Length)).Select(g => baseline.TrimEnd() + g).ToArray();
    }

    private static string VersionFromContent(string content)
        => Convert.ToHexStringLower(System.Security.Cryptography.SHA256.HashData(System.Text.Encoding.UTF8.GetBytes(content)));
}

internal sealed class PromptOptimizationWorker(
    IServiceScopeFactory scopeFactory,
    IOptionsMonitor<PromptOptimizationOptions> options,
    ILogger<PromptOptimizationWorker> log) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                var opts = options.CurrentValue;
                if (opts.Enabled && opts.AutoPromote)
                {
                    using var scope = scopeFactory.CreateScope();
                    var optimizer = scope.ServiceProvider.GetRequiredService<IPromptOptimizationService>();
                    foreach (var suite in opts.DefaultSuites)
                        await optimizer.OptimizeAsync(suite, suite, true, stoppingToken).ConfigureAwait(false);
                }
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested) { }
            catch (Exception ex)
            {
                log.LogWarning(ex, "Prompt optimization pass failed");
            }

            await Task.Delay(TimeSpan.FromHours(Math.Clamp(options.CurrentValue.IntervalHours, 1, 168)), stoppingToken)
                .ConfigureAwait(false);
        }
    }
}
