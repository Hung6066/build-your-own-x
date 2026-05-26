using AgentGovernance;
using AgentGovernance.Security;
using Hope.Agent.Application.Abstractions;
using Hope.Agent.Application.Governance;
using Hope.Agent.Application.Security;
using Hope.Agent.Domain.Audit;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace Hope.Agent.Infrastructure.Security;

/// <summary>
/// Layered prompt-injection shield — Phase 2 of the AGT governance integration.
///
/// Two complementary detection layers run in series:
/// <list type="bullet">
///   <item><b>Layer 1</b> — <see cref="HeuristicPromptShield"/>: zero-alloc on clean input;
///         catches known jailbreak phrases, role-spoof regex, exfiltration patterns,
///         and runtime-learned adversarial signatures. Blocks immediately on match.</item>
///   <item><b>Layer 2</b> — AGT <see cref="PromptInjectionDetector"/> (High sensitivity by default):
///         ML-assisted detection that catches novel jailbreaks evading static patterns.
///         Runs only when Layer 1 allows the input through, keeping the hot path fast.</item>
/// </list>
///
/// Either layer can independently block the input. The combined result merges reasons
/// from both layers so that operators can see the full detection signal in logs.
///
/// Sensitivity is controlled via <c>Governance:Policies:InjectionDetectionSensitivity</c>
/// in <c>appsettings.json</c> (High / Medium / Low — default: High).
/// Use "Medium" in development to reduce false positives during experimentation.
/// </summary>
internal sealed class AgtPromptShield : IPromptShield, IDisposable
{
    private readonly HeuristicPromptShield _inner;
    private readonly GovernanceKernel _kernel;
    private readonly IServiceScopeFactory _scopeFactory;
    private readonly ILogger<AgtPromptShield> _log;

    public AgtPromptShield(
        HeuristicPromptShield inner,
        IOptions<GovernancePolicyOptions> options,
        IServiceScopeFactory scopeFactory,
        ILogger<AgtPromptShield> log)
    {
        _inner = inner;
        _scopeFactory = scopeFactory;
        _log = log;

        _kernel = new GovernanceKernel(new GovernanceOptions
        {
            // Audit is owned by AgtGovernanceGate (Phase 1) — avoid double-counting.
            EnableAudit = false,
            EnablePromptInjectionDetection = true,
            PromptInjectionConfig = new DetectionConfig
            {
                Sensitivity = options.Value.InjectionDetectionSensitivity,
                // No CustomPatterns: this kernel detects *injection*, not PHI.
                // PHI detection belongs to AgtGovernanceGate.ScanForForbiddenPatterns().
            }
        });
    }

    public PromptShieldResult Inspect(string input)
    {
        // ── Layer 1: heuristic (synchronous, zero-alloc on clean path) ──────────
        var heuristic = _inner.Inspect(input);
        if (!heuristic.Allowed)
            return heuristic;

        // ── Layer 2: AGT ML-assisted injection detection ─────────────────────────
        var detector = _kernel.InjectionDetector;
        if (detector is null)
            return heuristic;   // AGT not configured — fall through to heuristic result

        DetectionResult agt;
        try
        {
            agt = detector.Detect(input);
        }
        catch (Exception ex)
        {
            // Never block on detector failure — log at Debug to avoid noise in tests.
            _log.LogDebug(ex, "AGT PromptInjectionDetector threw; returning heuristic result");
            return heuristic;
        }

        if (!agt.IsInjection)
            return heuristic;

        // Merge reasons from both layers for observability.
        var matched = agt.MatchedPatterns;
        var reasons = new List<string>(heuristic.Reasons.Count + (matched?.Count ?? 0));
        reasons.AddRange(heuristic.Reasons);
        if (matched is { Count: > 0 })
            foreach (var p in matched) reasons.Add($"agt:{p}");

        _log.LogWarning(
            "AGT shield blocked input (ThreatLevel={Level}): [{Patterns}]",
            agt.ThreatLevel,
            string.Join(", ", matched ?? []));

        // Phase 3 — audit trail: write AGT-layer blocks to audit_events (fire-and-forget).
        // Heuristic-layer blocks are already captured by HeuristicPromptShield's structured logs.
        _ = WriteShieldAuditAsync(reasons);

        return new PromptShieldResult(false, heuristic.SanitizedInput, reasons);
    }

    public void Dispose()
    {
        if (_kernel is IDisposable d) d.Dispose();
    }

    private async Task WriteShieldAuditAsync(IReadOnlyList<string> reasons)
    {
        try
        {
            await using var scope = _scopeFactory.CreateAsyncScope();
            var sink = scope.ServiceProvider.GetRequiredService<IAuditSink>();
            await sink.WriteAsync(new AuditEvent
            {
                Id = Guid.CreateVersion7(),
                OccurredAt = DateTimeOffset.UtcNow,
                Actor = "system:prompt_shield",
                Action = "security.injection.blocked",
                ResourceType = "input",
                Reason = "AGT PromptInjectionDetector detected injection pattern",
                PayloadJson = System.Text.Json.JsonSerializer.Serialize(new { reasons }),
            }, CancellationToken.None);
        }
        catch (Exception ex)
        {
            // Audit failures must never affect the shield decision.
            _log.LogDebug(ex, "AgtPromptShield audit write failed");
        }
    }
}
