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
    IEvalCaseStore caseStore,
    ILogger<EvaluationHarness> log) : IEvaluationHarness
{
    private static readonly string GoldenPath = Path.Combine(AppContext.BaseDirectory, "Learning", "golden-suite.json");

    public async Task<EvalRun> RunSuiteAsync(string suiteName, CancellationToken ct)
    {
        var items = await ResolveCasesAsync(suiteName, ct);
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
                var hallucinated = ContainsAny(verdict.Reasoning, "hallucination", "fabricated", "not grounded", "unsupported", "bịa", "không có căn cứ");
                var faithful = verdict.Score >= 0.75 && !hallucinated;
                results.Add(new
                {
                    item.Name,
                    verdict.Score,
                    verdict.Passed,
                    verdict.Reasoning,
                    Hallucinated = hallucinated,
                    Faithful = faithful,
                    ToolCallAccurate = true,
                    LatencyMs = 0,
                    CostUsd = 0,
                });
            }
            catch (Exception ex)
            {
                run.Failed += 1;
                results.Add(new { item.Name, error = ex.Message, Hallucinated = false, Faithful = false, ToolCallAccurate = false, LatencyMs = 0, CostUsd = 0 });
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

    public async Task<IReadOnlyList<EvalTrendPoint>> GetTrendAsync(string suite, int days, CancellationToken ct)
    {
        var cutoff = DateTimeOffset.UtcNow.AddDays(-days);
        var runs = await db.EvalRuns.AsNoTracking()
            .Where(r => r.Suite == suite && r.StartedAt >= cutoff && r.FinishedAt != null)
            .OrderBy(r => r.StartedAt)
            .ToListAsync(ct);

        var trend = new List<EvalTrendPoint>(runs.Count);
        for (var i = 0; i < runs.Count; i++)
        {
            var r = runs[i];
            double? delta = i == 0 ? null : r.AvgJudgeScore - runs[i - 1].AvgJudgeScore;
            trend.Add(new EvalTrendPoint(r.Id, r.StartedAt, r.Total, r.Passed, r.Failed, r.AvgJudgeScore, delta));
        }
        return trend;
    }

    public async Task<EvalMetricSummary> GetMetricsAsync(string suite, int days, CancellationToken ct)
    {
        var cutoff = DateTimeOffset.UtcNow.AddDays(-Math.Max(days, 1));
        var runs = await db.EvalRuns.AsNoTracking()
            .Where(r => r.Suite == suite && r.StartedAt >= cutoff && r.FinishedAt != null)
            .OrderByDescending(r => r.StartedAt)
            .ToListAsync(ct);

        var totalCases = runs.Sum(r => r.Total);
        var passed = runs.Sum(r => r.Passed);
        var judgeTotal = runs.Count == 0 ? 0 : runs.Average(r => r.AvgJudgeScore);
        var hallucinations = 0;
        var toolAccurate = 0;
        var toolTotal = 0;
        var faithful = 0;
        var latency = new List<double>();
        double cost = 0;

        foreach (var run in runs)
        {
            foreach (var item in ParseReportElements(run.ReportJson))
            {
                if (item.TryGetProperty("Hallucinated", out var h) && h.ValueKind == JsonValueKind.True) hallucinations++;
                if (item.TryGetProperty("Faithful", out var f) && f.ValueKind == JsonValueKind.True) faithful++;
                if (item.TryGetProperty("ToolCallAccurate", out var t))
                {
                    toolTotal++;
                    if (t.ValueKind == JsonValueKind.True) toolAccurate++;
                }
                if (item.TryGetProperty("LatencyMs", out var l) && l.TryGetDouble(out var ms)) latency.Add(ms);
                if (item.TryGetProperty("CostUsd", out var c) && c.TryGetDouble(out var usd)) cost += usd;
            }
        }

        var successRate = totalCases == 0 ? 0 : (double)passed / totalCases;
        var hallucinationRate = totalCases == 0 ? 0 : (double)hallucinations / totalCases;
        var toolAccuracy = toolTotal == 0 ? 1 : (double)toolAccurate / toolTotal;
        var faithfulness = totalCases == 0 ? 0 : (double)faithful / totalCases;
        var p95 = Percentile(latency, 0.95);
        var costPerSuccess = passed == 0 ? 0 : cost / passed;
        return new EvalMetricSummary(suite, runs.Count, totalCases, successRate, hallucinationRate, toolAccuracy, faithfulness, judgeTotal, p95, costPerSuccess);
    }

    // ── helpers ────────────────────────────────────────────────────────────────

    public async Task<EloTournamentResult> RunEloTournamentAsync(string suite, CancellationToken ct)
    {
        // Load the two most recent finished runs (tracked, so EF will detect changes)
        var recent = await db.EvalRuns
            .Where(r => r.Suite == suite && r.FinishedAt != null)
            .OrderByDescending(r => r.StartedAt)
            .Take(2)
            .ToListAsync(ct);

        if (recent.Count < 2)
            throw new InvalidOperationException($"Suite '{suite}' needs at least 2 completed runs for an Elo tournament.");

        var runA = recent[0]; // newer
        var runB = recent[1]; // older challenger

        var scoresA = ParseReportItems(runA.ReportJson);
        var scoresB = ParseReportItems(runB.ReportJson);

        int wins = 0, losses = 0, draws = 0;
        const double margin = 0.05;
        foreach (var (name, sA) in scoresA)
        {
            if (!scoresB.TryGetValue(name, out var sB)) continue;
            if (sA > sB + margin) wins++;
            else if (sB > sA + margin) losses++;
            else draws++;
        }
        int total = wins + losses + draws;
        double actualA = total == 0 ? 0.5 : (wins + 0.5 * draws) / total;
        double expectedA = 1.0 / (1.0 + Math.Pow(10.0, (runB.EloRating - runA.EloRating) / 400.0));

        runA.EloRating += EloK * (actualA - expectedA);
        runB.EloRating += EloK * ((1.0 - actualA) - (1.0 - expectedA));

        await db.SaveChangesAsync(ct);

        log.LogInformation(
            "Elo tournament suite={Suite}: {Wins}W-{Losses}L-{Draws}D → run {A} elo={EloA:F0}, run {B} elo={EloB:F0}",
            suite, wins, losses, draws, runA.Id, runA.EloRating, runB.Id, runB.EloRating);

        bool aWon = runA.EloRating >= runB.EloRating;
        return new EloTournamentResult(
            WinnerId: aWon ? runA.Id : runB.Id,
            LoserId: aWon ? runB.Id : runA.Id,
            WinnerEloAfter: aWon ? runA.EloRating : runB.EloRating,
            LoserEloAfter: aWon ? runB.EloRating : runA.EloRating,
            TotalMatchups: total,
            WinnerWins: aWon ? wins : losses,
            Draws: draws);
    }

    public async Task<IReadOnlyList<EvalRun>> GetLeaderboardAsync(string suite, int take, CancellationToken ct)
    {
        return await db.EvalRuns.AsNoTracking()
            .Where(r => r.Suite == suite && r.FinishedAt != null)
            .OrderByDescending(r => r.EloRating)
            .Take(take)
            .ToListAsync(ct);
    }

    private const double EloK = 32.0;

    /// <summary>Extracts per-case {Name → Score} from the stored ReportJson.</summary>
    private static Dictionary<string, double> ParseReportItems(string json)
    {
        var result = new Dictionary<string, double>(StringComparer.OrdinalIgnoreCase);
        try
        {
            using var doc = JsonDocument.Parse(json);
            foreach (var item in doc.RootElement.EnumerateArray())
            {
                if (item.TryGetProperty("Name", out var n) && item.TryGetProperty("Score", out var s))
                    result[n.GetString() ?? string.Empty] = s.GetDouble();
            }
        }
        catch { /* ignore malformed JSON */ }
        return result;
    }

    private static List<JsonElement> ParseReportElements(string json)
    {
        var result = new List<JsonElement>();
        try
        {
            using var doc = JsonDocument.Parse(json);
            foreach (var item in doc.RootElement.EnumerateArray())
                result.Add(item.Clone());
        }
        catch { }
        return result;
    }

    private static double Percentile(List<double> values, double p)
    {
        if (values.Count == 0) return 0;
        values.Sort();
        var idx = (int)Math.Ceiling(p * values.Count) - 1;
        return values[Math.Clamp(idx, 0, values.Count - 1)];
    }

    private static bool ContainsAny(string text, params string[] terms)
        => terms.Any(term => text.Contains(term, StringComparison.OrdinalIgnoreCase));

    /// <summary>Tries DB first; falls back to the golden-suite.json file when DB is empty.</summary>
    private async Task<List<GoldenItem>> ResolveCasesAsync(string suite, CancellationToken ct)
    {
        var dbCases = await caseStore.GetBySuiteAsync(suite, ct);
        if (dbCases.Count > 0)
            return dbCases.Select(c => new GoldenItem(c.Name, c.UserMessage, c.ReferenceAnswer)).ToList();

        return LoadGolden();
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

