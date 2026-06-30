using Hope.Agent.Application.Workflows;
using Hope.Agent.Workflows.Activities;
using Microsoft.Extensions.Logging;
using Temporalio.Common;
using Temporalio.Exceptions;
using Temporalio.Workflows;

namespace Hope.Agent.Workflows.WorkflowsImpl;

/// <summary>
/// Multi-step patient admission workflow with insurance verification, doctor assignment,
/// lab ordering, human approval gates, and discharge planning. Durable across replicas.
/// </summary>
[Workflow]
public class PatientAdmissionWorkflow
{
    private ApprovalDecision? insuranceOverride;
    private ApprovalDecision? dischargeApproval;
    private string status = "initializing";
    private readonly List<string> stepLog = new();

    [WorkflowRun]
    public async Task<PatientAdmissionResult> RunAsync(PatientAdmissionInput input)
    {
        var actOpts = WorkflowCommon.DefaultActivityOptions();

        Workflow.Logger.LogInformation("Admission workflow started for patient {Patient}", input.PatientId);
        status = "verifying-insurance";
        stepLog.Add(status);

        var insuranceCtx = new Dictionary<string, string>
        {
            ["patient_id"] = input.PatientId.ToString(),
            ["insurance_provider"] = input.InsuranceProvider ?? "unknown",
        };
        var insuranceDispatch = new AgentDispatchInput(
            input.UserId, "insurance", $"Verify coverage for: {input.ReasonForAdmission}", insuranceCtx, null, null, input.Priority);
        var insuranceResult = await Workflow.ExecuteActivityAsync(
            (ClinicalActivities a) => a.DispatchAgentAsync(insuranceDispatch),
            actOpts);
        stepLog.Add($"insurance:{insuranceResult.Role}");

        if (insuranceResult.Output.Contains("denied", StringComparison.OrdinalIgnoreCase))
        {
            status = "awaiting-insurance-approval";
            stepLog.Add(status);
            var deniedNotify = new NotificationActivityInput(
                "approval", "insurance.denied", "Insurance verification denied",
                "Manual override required to proceed with admission.",
                input.UserId, null);
            await Workflow.ExecuteActivityAsync(
                (ClinicalActivities a) => a.NotifyAsync(deniedNotify),
                actOpts);

            var ok = await Workflow.WaitConditionAsync(
                () => insuranceOverride is not null,
                TimeSpan.FromHours(24));
            if (!ok || insuranceOverride is null || !insuranceOverride.Approved)
            {
                status = "rejected";
                throw new ApplicationFailureException("Admission rejected: insurance denied and no override granted", "InsuranceDenied", nonRetryable: true);
            }
            stepLog.Add($"insurance-override:{insuranceOverride.ApproverId}");
        }

        status = "assigning-doctor";
        stepLog.Add(status);
        var doctorCtx = new Dictionary<string, string>
        {
            ["patient_id"] = input.PatientId.ToString(),
            ["preferred_doctor"] = input.PreferredDoctorId ?? string.Empty,
        };
        var doctorDispatch = new AgentDispatchInput(
            input.UserId, "scheduling", $"Assign on-call doctor for: {input.ReasonForAdmission}", doctorCtx, null, null, input.Priority);
        var doctorResult = await Workflow.ExecuteActivityAsync(
            (ClinicalActivities a) => a.DispatchAgentAsync(doctorDispatch),
            actOpts);
        stepLog.Add($"doctor:{doctorResult.Role}");

        status = "ordering-labs";
        stepLog.Add(status);
        var labCtx = new Dictionary<string, string> { ["patient_id"] = input.PatientId.ToString() };
        var labDispatch = new AgentDispatchInput(
            input.UserId, "clinical", $"Recommend initial labs for: {input.ReasonForAdmission}", labCtx, null, null, 5);
        var labResult = await Workflow.ExecuteActivityAsync(
            (ClinicalActivities a) => a.DispatchAgentAsync(labDispatch),
            actOpts);
        stepLog.Add($"labs:{labResult.Role}");

        status = "monitoring";
        stepLog.Add(status);
        var careNotify = new NotificationActivityInput(
            "care-team", "admission.in-progress", "Patient admitted",
            $"Patient {input.PatientId} admitted under {doctorResult.Output}.",
            input.UserId, null);
        await Workflow.ExecuteActivityAsync(
            (ClinicalActivities a) => a.NotifyAsync(careNotify),
            actOpts);

        await Workflow.DelayAsync(TimeSpan.FromMinutes(1));

        status = "awaiting-discharge-approval";
        stepLog.Add(status);
        var dischargeOk = await Workflow.WaitConditionAsync(
            () => dischargeApproval is not null,
            TimeSpan.FromDays(7));
        if (!dischargeOk || dischargeApproval is null)
        {
            status = "timed-out";
            throw new ApplicationFailureException("Discharge approval window elapsed", "DischargeTimeout");
        }

        if (!dischargeApproval.Approved)
        {
            status = "discharge-denied";
            stepLog.Add($"discharge-denied:{dischargeApproval.Reason}");
            return new PatientAdmissionResult(input.PatientId, "discharge-denied", dischargeApproval.Reason ?? "denied", stepLog);
        }

        status = "discharging";
        stepLog.Add(status);
        var dischargeCtx = new Dictionary<string, string> { ["patient_id"] = input.PatientId.ToString() };
        var dischargeDispatch = new AgentDispatchInput(
            input.UserId, "clinical", "Generate discharge plan and follow-up instructions", dischargeCtx, null, null, 5);
        var dischargeResult = await Workflow.ExecuteActivityAsync(
            (ClinicalActivities a) => a.DispatchAgentAsync(dischargeDispatch),
            actOpts);
        stepLog.Add($"discharge:{dischargeResult.Role}");

        status = "completed";
        stepLog.Add(status);
        return new PatientAdmissionResult(input.PatientId, status, dischargeResult.Output, stepLog);
    }

    [WorkflowSignal]
    public async Task ApproveInsuranceAsync(ApprovalDecision decision) => insuranceOverride = decision;

    [WorkflowSignal]
    public async Task ApproveDischargeAsync(ApprovalDecision decision) => dischargeApproval = decision;

    [WorkflowQuery]
    public string GetStatus() => status;

    [WorkflowQuery]
    public IReadOnlyList<string> GetSteps() => stepLog.ToArray();
}

public sealed record PatientAdmissionResult(Guid PatientId, string FinalStatus, string Summary, IReadOnlyList<string> Steps);
