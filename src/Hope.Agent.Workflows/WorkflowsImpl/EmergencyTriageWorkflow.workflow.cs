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

        if (severity >= 4)
        {
            var severityNotify = new NotificationActivityInput(
                "emergency", "triage.high-severity", $"High-severity emergency (level {severity})",
                input.Symptoms, null, new Dictionary<string, string> { ["level"] = severity.ToString() });
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
            return new EmergencyTriageResult(input.PatientId, severity, "no-show", stepLog);
        }

        status = "completed";
        stepLog.Add(status);
        return new EmergencyTriageResult(input.PatientId, severity, triage.Output, stepLog);
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
}

public sealed record EmergencyTriageResult(Guid PatientId, int Severity, string Summary, IReadOnlyList<string> Steps);
