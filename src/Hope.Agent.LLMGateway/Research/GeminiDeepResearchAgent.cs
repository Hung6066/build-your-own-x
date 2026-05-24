using System.Net.Http.Json;
using System.Text.Json;
using System.Text.Json.Serialization;
using Hope.Agent.Application.Research;
using Hope.Agent.LLMGateway.Providers;
using Microsoft.Extensions.Logging;

namespace Hope.Agent.LLMGateway.Research;

/// <summary>
/// Uses Gemini's <c>generateContent</c> API with the <c>google_search</c> grounding tool
/// to run a multi-step Deep Research pass.  Mirrors what Google's Deep Research product does:
/// expand the query → search → synthesise → cite sources.
///
/// In Fast mode: single grounded call with gemini-2.5-flash.
/// In Max mode:  three-phase chain (plan → search → synthesise) for richer reports.
/// </summary>
internal sealed class GeminiDeepResearchAgent(
    HttpClient http,
    GeminiOptions options,
    ILogger<GeminiDeepResearchAgent> log) : IDeepResearchAgent
{
    private static readonly JsonSerializerOptions JsonOpts =
        new(JsonSerializerDefaults.Web) { DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull };

    public async Task<ResearchReport> ResearchAsync(ResearchRequest request, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(options.ApiKey))
            throw new InvalidOperationException(
                "Gemini ApiKey is not configured. Set LLM:Gemini:ApiKey to enable Deep Research.");

        var model = request.Mode == ResearchMode.Max
            ? options.DeepResearchModel
            : options.Model;

        log.LogInformation("DeepResearch start: mode={Mode} model={Model} query={Query}",
            request.Mode, model, request.Query);

        var report = request.Mode == ResearchMode.Max
            ? await RunMaxAsync(model, request, ct)
            : await RunFastAsync(model, request, ct);

        log.LogInformation("DeepResearch done: {Title} ({Citations} citations)",
            report.Title, report.Citations.Count);
        return report;
    }

    // ── Fast: single grounded call ──────────────────────────────────────────────

    private async Task<ResearchReport> RunFastAsync(string model, ResearchRequest req, CancellationToken ct)
    {
        var systemInstruction = BuildSystemInstruction(req.MaxSources);
        var payload = BuildPayload(model, systemInstruction, req.Query, groundingEnabled: true);

        var (text, citations) = await CallGeminiAsync(model, payload, ct);
        return ParseReport(text, citations, model, req.Query);
    }

    // ── Max: plan → search → synthesise (three hops) ───────────────────────────

    private async Task<ResearchReport> RunMaxAsync(string model, ResearchRequest req, CancellationToken ct)
    {
        // Phase 1: Generate a research plan with sub-questions
        var planPrompt = $$"""
            You are a research planner.  Given this question, produce a concise JSON array of 3-5 sub-questions
            that together cover all aspects of the topic.
            Return STRICT JSON only: {"subQuestions":["...","..."]}

            QUESTION: {{req.Query}}
            """;
        var planPayload = BuildPayload(model, null, planPrompt, groundingEnabled: false);
        var (planText, _) = await CallGeminiAsync(model, planPayload, ct);
        var subQuestions = ParseSubQuestions(planText, req.Query);

        // Phase 2: Run grounded search for each sub-question, accumulate evidence
        var evidence = new System.Text.StringBuilder();
        var allCitations = new List<string>();
        foreach (var q in subQuestions)
        {
            var searchPayload = BuildPayload(model, null, q, groundingEnabled: true);
            var (text, cites) = await CallGeminiAsync(model, searchPayload, ct);
            evidence.AppendLine($"### Sub-question: {q}");
            evidence.AppendLine(text);
            evidence.AppendLine();
            allCitations.AddRange(cites);
        }

        // Phase 3: Synthesise all evidence into a final report
        var synthesisInstruction = BuildSystemInstruction(req.MaxSources);
        var synthesisPrompt = $"""
            Synthesise the following research evidence into a comprehensive, well-structured
            markdown report answering: {req.Query}

            Include an Executive Summary section, then detailed findings, then a Conclusion.

            EVIDENCE:
            {evidence}
            """;
        var synthPayload = BuildPayload(model, synthesisInstruction, synthesisPrompt, groundingEnabled: false);
        var (finalText, _) = await CallGeminiAsync(model, synthPayload, ct);

        return ParseReport(finalText, allCitations.Distinct(StringComparer.OrdinalIgnoreCase).ToList(), model, req.Query);
    }

    // ── Helpers ─────────────────────────────────────────────────────────────────

    private static string BuildSystemInstruction(int maxSources) => $"""
        You are a senior research analyst.  Provide well-structured markdown reports with clear sections.
        Always cite sources in [1] style at the end.  Aim for at most {maxSources} distinct citations.
        Never fabricate facts; if uncertain, say so explicitly.
        """;

    private static object BuildPayload(string model, string? systemInstruction, string userQuery, bool groundingEnabled)
    {
        var tools = groundingEnabled
            ? new object[] { new { google_search = new { } } }
            : Array.Empty<object>();

        return systemInstruction is null
            ? new
            {
                contents = new[] { new { role = "user", parts = new[] { new { text = userQuery } } } },
                tools,
                generationConfig = new { temperature = 0.2, maxOutputTokens = 8192 },
            }
            : new
            {
                system_instruction = new { parts = new[] { new { text = systemInstruction } } },
                contents = new[] { new { role = "user", parts = new[] { new { text = userQuery } } } },
                tools,
                generationConfig = new { temperature = 0.2, maxOutputTokens = 8192 },
            };
    }

    private async Task<(string Text, List<string> Citations)> CallGeminiAsync(
        string model, object payload, CancellationToken ct)
    {
        var url = $"models/{model}:generateContent?key={options.ApiKey}";
        using var resp = await http.PostAsJsonAsync(url, payload, JsonOpts, ct);
        resp.EnsureSuccessStatusCode();
        var json = await resp.Content.ReadFromJsonAsync<JsonElement>(JsonOpts, ct);

        var text = string.Empty;
        var citations = new List<string>();

        if (json.TryGetProperty("candidates", out var cands) && cands.GetArrayLength() > 0)
        {
            var cand = cands[0];
            if (cand.TryGetProperty("content", out var content) &&
                content.TryGetProperty("parts", out var parts))
            {
                foreach (var part in parts.EnumerateArray())
                {
                    if (part.TryGetProperty("text", out var t))
                        text += t.GetString();
                }
            }

            // Extract grounding citations from groundingMetadata
            if (cand.TryGetProperty("groundingMetadata", out var gm) &&
                gm.TryGetProperty("groundingChunks", out var chunks))
            {
                foreach (var chunk in chunks.EnumerateArray())
                {
                    if (chunk.TryGetProperty("web", out var web) &&
                        web.TryGetProperty("uri", out var uri))
                    {
                        var uriStr = uri.GetString();
                        if (!string.IsNullOrWhiteSpace(uriStr))
                            citations.Add(uriStr);
                    }
                }
            }
        }

        return (text, citations);
    }

    private static ResearchReport ParseReport(string text, IReadOnlyList<string> citations, string model, string query)
    {
        // Extract title from first # heading; fall back to query
        var title = query;
        foreach (var line in text.Split('\n'))
        {
            var trimmed = line.TrimStart('#', ' ');
            if (trimmed.Length > 5 && line.StartsWith('#'))
            {
                title = trimmed.Trim();
                break;
            }
        }

        // Executive summary = first non-empty paragraph after the title
        var summary = string.Empty;
        var inBody = false;
        foreach (var line in text.Split('\n'))
        {
            if (!inBody && line.StartsWith('#')) { inBody = true; continue; }
            if (inBody && !string.IsNullOrWhiteSpace(line) && !line.StartsWith('#'))
            {
                summary = line.Trim();
                break;
            }
        }

        return new ResearchReport(
            Title: title,
            Summary: summary.Length > 0 ? summary : text[..Math.Min(300, text.Length)].Trim(),
            FullContent: text,
            Citations: citations,
            GeneratedAt: DateTimeOffset.UtcNow,
            Model: model);
    }

    private static List<string> ParseSubQuestions(string planJson, string fallbackQuery)
    {
        try
        {
            var start = planJson.IndexOf('{');
            var end = planJson.LastIndexOf('}');
            if (start >= 0 && end > start)
            {
                using var doc = JsonDocument.Parse(planJson.Substring(start, end - start + 1));
                if (doc.RootElement.TryGetProperty("subQuestions", out var arr))
                    return arr.EnumerateArray().Select(e => e.GetString() ?? string.Empty)
                        .Where(s => s.Length > 0).ToList();
            }
        }
        catch { /* fall through */ }
        return [fallbackQuery];
    }

    public static void Configure(HttpClient client, GeminiOptions opts)
    {
        client.BaseAddress = new Uri(opts.BaseUrl.TrimEnd('/') + '/');
        client.Timeout = TimeSpan.FromSeconds(300); // research can take longer
    }
}
