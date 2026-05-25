using Hope.Agent.Application.Workflows;
using Hope.Agent.Workflows.Activities;
using Microsoft.Extensions.Logging;
using Temporalio.Common;
using Temporalio.Workflows;

namespace Hope.Agent.Workflows.WorkflowsImpl;

/// <summary>
/// Emergency triage workflow: assess severity, escalate, notify, and run parallel
/// pre-admission tasks (insurance + imaging slot). Failures retry with backoff.
/// </summary>
[Workflow]
public class EmergencyTriageWorkflow
{
    private string status = "initializing";
    private bool patientArrived;
    private readonly List<string> stepLog = new();

    [WorkflowRun]
    public async Task<EmergencyTriageResult> RunAsync(EmergencyTriageInput input)
    {
        var actOpts = new ActivityOptions
        {
            StartToCloseTimeout = TimeSpan.FromMinutes(1),
            RetryPolicy = new RetryPolicy
            {
                InitialInterval = TimeSpan.FromSeconds(1),
                BackoffCoefficient = 2.0F,
                MaximumInterval = TimeSpan.FromSeconds(30),
                MaximumAttempts = 6,
            },
        };

        Workflow.Logger.LogInformation("Triage workflow started for patient {Patient}", input.PatientId);
        status = "triaging";
        stepLog.Add(status);

        var triageCtx = new Dictionary<string, string>
        {
            ["patient_id"] = input.PatientId.ToString(),
            ["location"] = input.Location ?? "unknown",
        };
        var triageDispatch = new AgentDispatchInput(
            input.UserId, "emergency", input.Symptoms, triageCtx, null, null, 1);
        var triage = await Workflow.ExecuteActivityAsync(
            (ClinicalActivities a) => a.DispatchAgentAsync(triageDispatch),
            actOpts);
        stepLog.Add($"triage:{triage.Role}");

        var severity = ParseSeverity(triage.Output);
        status = $"triaged-level-{severity}";
        stepLog.Add(status);

        // ── Priority ranking via Weighted EDF scoring ────────────────────────
        // Even for a single patient this computes a normalized priority_score that
        // surfaces triage reasoning (severity, risk flags, resource load) on the
        // workflow timeline and can be used for multi-patient comparisons later.
        status = "ranking-priority";
        var rankCtx = new Dictionary<string, string>
        {
            ["patient_id"] = input.PatientId.ToString(),
            ["severity_level"] = severity.ToString(),
            ["risk_flags"] = ExtractRiskFlags(input.Symptoms),
        };
        var rankDispatch = new AgentDispatchInput(
            input.UserId, "rank_triage",
            $"Score priority for emergency patient level {severity}", rankCtx, null, null, 1);
        var rankResult = await Workflow.ExecuteActivityAsync(
            (ClinicalActivities a) => a.DispatchAgentAsync(rankDispatch), actOpts);
        var priorityScore = ExtractPriorityScore(rankResult.Output);
        stepLog.Add($"priority-score:{priorityScore:F1}");

        if (severity >= 4)
        {
            var severityNotify = new NotificationActivityInput(
                "emergency", "triage.high-severity", $"High-severity emergency (level {severity}, score {priorityScore:F0})",
                input.Symptoms, null, new Dictionary<string, string> { ["level"] = severity.ToString(), ["priority_score"] = priorityScore.ToString("F1") });
            var notifyTask = Workflow.ExecuteActivityAsync(
                (ClinicalActivities a) => a.NotifyAsync(severityNotify),
                actOpts);

            var imagingCtx = new Dictionary<string, string> { ["patient_id"] = input.PatientId.ToString(), ["urgency"] = "stat" };
            var imagingDispatch = new AgentDispatchInput(
                input.UserId, "scheduling", "Reserve emergency imaging slot", imagingCtx, null, null, 5);
            var imagingTask = Workflow.ExecuteActivityAsync(
                (ClinicalActivities a) => a.DispatchAgentAsync(imagingDispatch),
                actOpts);

            var insuranceCtx = new Dictionary<string, string> { ["patient_id"] = input.PatientId.ToString() };
            var insuranceDispatch = new AgentDispatchInput(
                input.UserId, "insurance", "Fast-track emergency coverage check", insuranceCtx, null, null, 5);
            var insuranceTask = Workflow.ExecuteActivityAsync(
                (ClinicalActivities a) => a.DispatchAgentAsync(insuranceDispatch),
                actOpts);

            await Workflow.WhenAllAsync(new Task[] { notifyTask, imagingTask, insuranceTask });
            stepLog.Add("fanout:notify+imaging+insurance");
        }

        status = "awaiting-arrival";
        stepLog.Add(status);
        var arrived = await Workflow.WaitConditionAsync(() => patientArrived, TimeSpan.FromHours(2));
        if (!arrived)
        {
            status = "no-show";
            stepLog.Add(status);
            var noShowNotify = new NotificationActivityInput(
                "emergency", "triage.no-show", "Patient did not arrive", input.Symptoms, null, null);
            await Workflow.ExecuteActivityAsync(
                (ClinicalActivities a) => a.NotifyAsync(noShowNotify),
                actOpts);
            return new EmergencyTriageResult(input.PatientId, severity, priorityScore, "no-show", stepLog);
        }

        status = "completed";
        stepLog.Add(status);
        return new EmergencyTriageResult(input.PatientId, severity, priorityScore, triage.Output, stepLog);
    }

    [WorkflowSignal]
    public async Task PatientArrivedAsync() => patientArrived = true;

    [WorkflowQuery]
    public string GetStatus() => status;

    [WorkflowQuery]
    public IReadOnlyList<string> GetSteps() => stepLog.ToArray();

    private static int ParseSeverity(string output)
    {
        // Look for "level X" / "Level: X" patterns; fall back to 3.
        for (var i = 0; i < output.Length - 1; i++)
        {
            if (char.IsDigit(output[i]) && output[i] >= '1' && output[i] <= '5')
            {
                return output[i] - '0';
            }
        }
        return 3;
    }

    /// <summary>Extracts a comma-separated list of risk flag keywords from the symptom description.</summary>
    private static string ExtractRiskFlags(string symptoms)
    {
        var flags = new List<string>(4);
        var lower = symptoms.ToLowerInvariant();
        if (lower.Contains("chest") || lower.Contains("đau ngực")) flags.Add("chest_pain");
        if (lower.Contains("breath") || lower.Contains("khó thở")) flags.Add("oxygen_below_90");
        if (lower.Contains("unconscious") || lower.Contains("hôn mê")) flags.Add("unconscious");
        if (lower.Contains("stroke") || lower.Contains("đột quỵ")) flags.Add("stroke_symptoms");
        if (lower.Contains("sepsis") || lower.Contains("nhiễm khuẩn")) flags.Add("sepsis");
        return string.Join(",", flags);
    }

    private static double ExtractPriorityScore(string rankOutput)
    {
        try
        {
            using var doc = System.Text.Json.JsonDocument.Parse(rankOutput);
            if (doc.RootElement.TryGetProperty("priority_score", out var ps))
                return ps.GetDouble();
        }
        catch { /* keep default */ }
        return 0;
    }
}

public sealed record EmergencyTriageResult(Guid PatientId, int Severity, double PriorityScore, string Summary, IReadOnlyList<string> Steps);
