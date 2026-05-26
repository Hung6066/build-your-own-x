using AgentGovernance;
using AgentGovernance.Integration;
using AgentGovernance.Security;
using Hope.Agent.Application.Governance;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace Hope.Agent.MultiAgent.Governance;

/// <summary>
/// Microsoft AGT-backed implementation of <see cref="IGovernanceGate"/>.
///
/// At construction:
///   • Loads all YAML policy files listed in <see cref="GovernancePolicyOptions.PolicyPaths"/>
///     (missing files emit a warning and are skipped — fail-open for dev environments).
///   • Configures <see cref="PromptInjectionDetector"/> with <c>CustomPatterns</c> set to
///     <see cref="GovernancePolicyOptions.PhiMarkers"/> for PHI detection.
///
/// Intent evaluation uses <see cref="GovernanceKernel.EvaluateToolCall"/> which evaluates
/// the loaded YAML policy rules against the intent name as <c>action.type</c>.
/// On evaluation exception the gate <b>fails closed</b> (denies the request).
/// </summary>
internal sealed class AgtGovernanceGate : IGovernanceGate, IDisposable
{
    private const string HopeAgentDid = "did:mesh:hope-agent";

    private readonly GovernanceKernel _kernel;
    private readonly bool _hasPolicies;
    private readonly ILogger<AgtGovernanceGate> _log;

    public AgtGovernanceGate(IOptions<GovernancePolicyOptions> options, ILogger<AgtGovernanceGate> log)
    {
        _log = log;
        var opts = options.Value;

        var existingPaths = new List<string>();
        foreach (var path in opts.PolicyPaths)
        {
            if (File.Exists(path))
                existingPaths.Add(path);
            else
                _log.LogWarning("AGT governance: policy file not found at '{Path}' — skipped. "
                    + "Ensure the file exists in production.", path);
        }

        _kernel = new GovernanceKernel(new GovernanceOptions
        {
            PolicyPaths = existingPaths,
            EnableAudit = true,
            EnableMetrics = false,
            EnablePromptInjectionDetection = true,
            PromptInjectionConfig = new DetectionConfig
            {
                Sensitivity = "High",
                CustomPatterns = [.. opts.PhiMarkers],
            },
        });

        _hasPolicies = existingPaths.Count > 0;
        if (!_hasPolicies)
            _log.LogWarning("AGT governance: no policy files loaded. Intent gate is in ALLOW-ALL mode. "
                + "Set 'Governance:Policies:PolicyPaths' in appsettings.json for production.");
        else
            _log.LogInformation("AGT governance: loaded {Count} policy file(s): {Paths}",
                existingPaths.Count, string.Join(", ", existingPaths));
    }

    public ValueTask<GovernanceDecision> EvaluateIntentAsync(
        string agentDid,
        string intent,
        IReadOnlyDictionary<string, object?>? context = null,
        CancellationToken ct = default)
    {
        // Without loaded policies the kernel has no rules — allow-all rather than deny unknown.
        if (!_hasPolicies)
            return ValueTask.FromResult(new GovernanceDecision(true, "no-policy"));

        var parameters = new Dictionary<string, object> { ["intent"] = intent };
        if (context is not null)
            foreach (var (k, v) in context)
                if (v is not null) parameters[k] = v;

        ToolCallResult result;
        try
        {
            result = _kernel.EvaluateToolCall(agentDid, intent, parameters);
        }
        catch (Exception ex)
        {
            _log.LogError(ex, "AGT governance evaluation threw for intent='{Intent}'; failing closed", intent);
            return ValueTask.FromResult(
                new GovernanceDecision(false, "error", null, "Governance evaluation error — failing closed"));
        }

        var decision = new GovernanceDecision(
            result.Allowed,
            result.PolicyDecision?.PolicyName ?? string.Empty,
            result.PolicyDecision?.MatchedRule,
            result.Reason);

        if (!result.Allowed)
            _log.LogWarning("AGT DENIED intent='{Intent}' agentDid='{Did}' rule='{Rule}' reason='{Reason}'",
                intent, agentDid, decision.MatchedRule, decision.DenyReason);

        return ValueTask.FromResult(decision);
    }

    public IReadOnlyList<string> ScanForForbiddenPatterns(string input)
    {
        if (string.IsNullOrWhiteSpace(input)) return [];

        var detector = _kernel.InjectionDetector;
        if (detector is null) return [];

        DetectionResult detection;
        try
        {
            detection = detector.Detect(input);
        }
        catch (Exception ex)
        {
            _log.LogWarning(ex, "AGT PHI scan threw; returning empty match list");
            return [];
        }

        return detection.MatchedPatterns is { Count: > 0 } mp ? mp : [];
    }

    public void Dispose() => _kernel.Dispose();
}
