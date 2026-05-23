using System.Text.Json;
using Hope.Agent.Application.LLM;
using Hope.Agent.Application.Learning;
using Hope.Agent.Domain.Learning;
using Hope.Agent.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;

namespace Hope.Agent.Infrastructure.Learning;

internal sealed record GoldenItem(string Name, string User, string Reference);

internal sealed class EvaluationHarness(
    AgentDbContext db,
    ILLMRouter router,
    IJudge judge,
    ILogger<EvaluationHarness> log) : IEvaluationHarness
{
    private static readonly string GoldenPath = Path.Combine(AppContext.BaseDirectory, "Learning", "golden-suite.json");

    public async Task<EvalRun> RunSuiteAsync(string suiteName, CancellationToken ct)
    {
        var items = LoadGolden();
        var run = new EvalRun
        {
            Id = Guid.CreateVersion7(),
            Suite = suiteName,
            StartedAt = DateTimeOffset.UtcNow,
            ReportJson = "[]",
            Total = items.Count,
        };
        db.EvalRuns.Add(run);
        await db.SaveChangesAsync(ct);

        var results = new List<object>();
        double scoreSum = 0;
        var chat = router.SelectChat();

        foreach (var item in items)
        {
            try
            {
                var resp = await chat.CompleteAsync(new ChatRequest(
                    Messages: new ChatMessage[]
                    {
                        new("system", "Bạn là trợ lý y tế cẩn trọng, tuân thủ quy định bảo mật."),
                        new("user", item.User),
                    },
                    Temperature: 0.2f,
                    MaxTokens: 512), ct);

                var verdict = await judge.ScoreAsync(item.User, resp.Content, item.Reference, ct);
                if (verdict.Passed) run.Passed += 1; else run.Failed += 1;
                scoreSum += verdict.Score;
                results.Add(new { item.Name, verdict.Score, verdict.Passed, verdict.Reasoning });
            }
            catch (Exception ex)
            {
                run.Failed += 1;
                results.Add(new { item.Name, error = ex.Message });
                log.LogWarning(ex, "Eval item {Name} threw", item.Name);
            }
        }

        run.AvgJudgeScore = items.Count == 0 ? 0 : scoreSum / items.Count;
        run.FinishedAt = DateTimeOffset.UtcNow;
        run.ReportJson = JsonSerializer.Serialize(results);
        await db.SaveChangesAsync(ct);
        return run;
    }

    public async Task<IReadOnlyList<EvalRun>> RecentRunsAsync(int take, CancellationToken ct)
    {
        return await db.EvalRuns.AsNoTracking()
            .OrderByDescending(r => r.StartedAt)
            .Take(take)
            .ToListAsync(ct);
    }

    private static List<GoldenItem> LoadGolden()
    {
        if (!File.Exists(GoldenPath)) return new();
        using var s = File.OpenRead(GoldenPath);
        using var doc = JsonDocument.Parse(s);
        var list = new List<GoldenItem>();
        foreach (var el in doc.RootElement.EnumerateArray())
        {
            list.Add(new GoldenItem(
                el.GetProperty("name").GetString() ?? "",
                el.GetProperty("user").GetString() ?? "",
                el.GetProperty("reference").GetString() ?? ""));
        }
        return list;
    }
}
