using Hope.Agent.Application.Observability;
using Hope.Agent.Application.Workflows;
using Hope.Agent.Workflows.WorkflowsImpl;
using Temporalio.Client;

namespace Hope.Agent.Workflows;

internal sealed class TemporalWorkflowDispatcher : IWorkflowDispatcher
{
    private readonly ITemporalClient client;
    private readonly TemporalOptions options;

    public TemporalWorkflowDispatcher(ITemporalClient client, TemporalOptions options)
    {
        this.client = client;
        this.options = options;
    }

    public async Task<WorkflowStartResult> StartPatientAdmissionAsync(PatientAdmissionInput input, string? workflowId = null, CancellationToken ct = default)
    {
        var id = workflowId ?? $"admission-{input.PatientId:N}-{Guid.CreateVersion7():N}";
        var handle = await client.StartWorkflowAsync(
            (PatientAdmissionWorkflow wf) => wf.RunAsync(input),
            new WorkflowOptions(id: id, taskQueue: options.TaskQueue)).ConfigureAwait(false);
        HopeMeters.WorkflowsStarted.Add(1, new KeyValuePair<string, object?>("workflow", "patient_admission"));
        return new WorkflowStartResult(handle.Id, handle.ResultRunId ?? string.Empty, DateTimeOffset.UtcNow);
    }

    public async Task<WorkflowStartResult> StartEmergencyTriageAsync(EmergencyTriageInput input, string? workflowId = null, CancellationToken ct = default)
    {
        var id = workflowId ?? $"triage-{input.PatientId:N}-{Guid.CreateVersion7():N}";
        var handle = await client.StartWorkflowAsync(
            (EmergencyTriageWorkflow wf) => wf.RunAsync(input),
            new WorkflowOptions(id: id, taskQueue: options.TaskQueue)).ConfigureAwait(false);
        HopeMeters.WorkflowsStarted.Add(1, new KeyValuePair<string, object?>("workflow", "emergency_triage"));
        return new WorkflowStartResult(handle.Id, handle.ResultRunId ?? string.Empty, DateTimeOffset.UtcNow);
    }

    public async Task<WorkflowStartResult> StartAppointmentSchedulingAsync(AppointmentSchedulingInput input, string? workflowId = null, CancellationToken ct = default)
    {
        var id = workflowId ?? $"scheduling-{input.PatientId:N}-{Guid.CreateVersion7():N}";
        var handle = await client.StartWorkflowAsync(
            (AppointmentSchedulingWorkflow wf) => wf.RunAsync(input),
            new WorkflowOptions(id: id, taskQueue: options.TaskQueue)).ConfigureAwait(false);
        HopeMeters.WorkflowsStarted.Add(1, new KeyValuePair<string, object?>("workflow", "appointment_scheduling"));
        return new WorkflowStartResult(handle.Id, handle.ResultRunId ?? string.Empty, DateTimeOffset.UtcNow);
    }

    public async Task<WorkflowStartResult> StartMedicationReminderAsync(MedicationReminderInput input, string? workflowId = null, CancellationToken ct = default)
    {
        var id = workflowId ?? $"reminder-{input.PatientId:N}-{Guid.CreateVersion7():N}";
        var handle = await client.StartWorkflowAsync(
            (MedicationReminderWorkflow wf) => wf.RunAsync(input),
            new WorkflowOptions(id: id, taskQueue: options.TaskQueue)).ConfigureAwait(false);
        HopeMeters.WorkflowsStarted.Add(1, new KeyValuePair<string, object?>("workflow", "medication_reminder"));
        return new WorkflowStartResult(handle.Id, handle.ResultRunId ?? string.Empty, DateTimeOffset.UtcNow);
    }

    public async Task SignalReminderConfirmationAsync(string workflowId, ReminderConfirmation confirmation, CancellationToken ct = default)
    {
        var handle = client.GetWorkflowHandle<MedicationReminderWorkflow>(workflowId);
        await handle.SignalAsync(wf => wf.ConfirmDoseAsync(confirmation)).ConfigureAwait(false);
    }

    public async Task<WorkflowStartResult> StartAuditReportAsync(AuditReportInput input, string? workflowId = null, CancellationToken ct = default)
    {
        var id = workflowId ?? $"audit-{input.ReportType}-{DateTimeOffset.UtcNow:yyyyMMdd}-{Guid.CreateVersion7():N}";
        var handle = await client.StartWorkflowAsync(
            (AuditReportWorkflow wf) => wf.RunAsync(input),
            new WorkflowOptions(id: id, taskQueue: options.TaskQueue)).ConfigureAwait(false);
        HopeMeters.WorkflowsStarted.Add(1, new KeyValuePair<string, object?>("workflow", "audit_report"));
        return new WorkflowStartResult(handle.Id, handle.ResultRunId ?? string.Empty, DateTimeOffset.UtcNow);
    }

    public async Task SignalApprovalAsync(string workflowId, ApprovalDecision decision, CancellationToken ct = default)
    {
        // Route signal by step name; "discharge" → discharge approval, anything else → insurance override.
        if (string.Equals(decision.Step, "discharge", StringComparison.OrdinalIgnoreCase))
        {
            var handle = client.GetWorkflowHandle<PatientAdmissionWorkflow>(workflowId);
            await handle.SignalAsync(wf => wf.ApproveDischargeAsync(decision)).ConfigureAwait(false);
            return;
        }
        if (string.Equals(decision.Step, "arrival", StringComparison.OrdinalIgnoreCase))
        {
            var handle = client.GetWorkflowHandle<EmergencyTriageWorkflow>(workflowId);
            await handle.SignalAsync(wf => wf.PatientArrivedAsync()).ConfigureAwait(false);
            return;
        }
        var admit = client.GetWorkflowHandle<PatientAdmissionWorkflow>(workflowId);
        await admit.SignalAsync(wf => wf.ApproveInsuranceAsync(decision)).ConfigureAwait(false);
    }

    public async Task<WorkflowStatus> GetStatusAsync(string workflowId, CancellationToken ct = default)
    {
        var handle = client.GetWorkflowHandle(workflowId);
        var desc = await handle.DescribeAsync().ConfigureAwait(false);
        string? result = null;
        try
        {
            var queryHandle = client.GetWorkflowHandle<PatientAdmissionWorkflow>(workflowId);
            result = await queryHandle.QueryAsync(wf => wf.GetStatus()).ConfigureAwait(false);
        }
        catch
        {
            try
            {
                var queryHandle = client.GetWorkflowHandle<EmergencyTriageWorkflow>(workflowId);
                result = await queryHandle.QueryAsync(wf => wf.GetStatus()).ConfigureAwait(false);
            }
            catch
            {
                try
                {
                    var queryHandle = client.GetWorkflowHandle<AppointmentSchedulingWorkflow>(workflowId);
                    result = await queryHandle.QueryAsync(wf => wf.GetStatus()).ConfigureAwait(false);
                }
                catch
                {
                    try
                    {
                        var queryHandle = client.GetWorkflowHandle<MedicationReminderWorkflow>(workflowId);
                        result = await queryHandle.QueryAsync(wf => wf.GetStatus()).ConfigureAwait(false);
                    }
                    catch
                    {
                        try
                        {
                            var queryHandle = client.GetWorkflowHandle<AuditReportWorkflow>(workflowId);
                            result = await queryHandle.QueryAsync(wf => wf.GetStatus()).ConfigureAwait(false);
                        }
                        catch
                        {
                            // workflow type unknown — fall through with just lifecycle status
                        }
                    }
                }
            }
        }

        return new WorkflowStatus(
            WorkflowId: workflowId,
            RunId: desc.RunId,
            Status: desc.Status.ToString(),
            StartedAt: desc.StartTime,
            ClosedAt: desc.CloseTime,
            Result: result,
            FailureReason: null);
    }

    public async Task CancelAsync(string workflowId, string reason, CancellationToken ct = default)
    {
        var handle = client.GetWorkflowHandle(workflowId);
        await handle.CancelAsync().ConfigureAwait(false);
    }
}
