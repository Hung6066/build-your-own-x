namespace Hope.Agent.Application.Research;

/// <summary>
/// Runs a Deep Research pass — multi-step grounded search that synthesises sources
/// into a structured report with citations.
/// Inspired by Gemini Deep Research Max (Google I/O 2025) and the MCP Atlas benchmark.
/// </summary>
public interface IDeepResearchAgent
{
    Task<ResearchReport> ResearchAsync(ResearchRequest request, CancellationToken ct);
}

/// <param name="Query">Natural-language research question.</param>
/// <param name="Mode">
///   <see cref="ResearchMode.Fast"/> uses a single grounded pass (gemini-2.5-flash).
///   <see cref="ResearchMode.Max"/> uses extended-thinking + multi-step reflection (gemini-2.5-pro or equivalent).
/// </param>
/// <param name="MaxSources">Upper bound on sources the agent tries to cite.</param>
public sealed record ResearchRequest(
    string Query,
    ResearchMode Mode = ResearchMode.Fast,
    int MaxSources = 20);

public enum ResearchMode { Fast, Max }

/// <param name="Title">Auto-generated report title.</param>
/// <param name="Summary">Executive summary (≤ 4 sentences).</param>
/// <param name="FullContent">Full structured markdown report.</param>
/// <param name="Citations">URLs / references surfaced by the model.</param>
/// <param name="GeneratedAt">UTC timestamp.</param>
/// <param name="Model">Gemini model that generated this report.</param>
public sealed record ResearchReport(
    string Title,
    string Summary,
    string FullContent,
    IReadOnlyList<string> Citations,
    DateTimeOffset GeneratedAt,
    string Model);
