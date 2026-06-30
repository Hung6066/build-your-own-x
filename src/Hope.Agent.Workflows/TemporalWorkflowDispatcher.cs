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
        EnsureWorkflowVersionGate();
        var id = BuildWorkflowId($"admission-{input.PatientId:N}", workflowId);
        var handle = await client.StartWorkflowAsync(
            (PatientAdmissionWorkflow wf) => wf.RunAsync(input),
            new WorkflowOptions(id: id, taskQueue: options.TaskQueue)).ConfigureAwait(false);
        HopeMeters.WorkflowsStarted.Add(1, new KeyValuePair<string, object?>("workflow", "patient_admission"));
        return new WorkflowStartResult(handle.Id, handle.ResultRunId ?? string.Empty, DateTimeOffset.UtcNow);
    }

    public async Task<WorkflowStartResult> StartEmergencyTriageAsync(EmergencyTriageInput input, string? workflowId = null, CancellationToken ct = default)
    {
        EnsureWorkflowVersionGate();
        var id = BuildWorkflowId($"triage-{input.PatientId:N}", workflowId);
        var handle = await client.StartWorkflowAsync(
            (EmergencyTriageWorkflow wf) => wf.RunAsync(input),
            new WorkflowOptions(id: id, taskQueue: options.TaskQueue)).ConfigureAwait(false);
        HopeMeters.WorkflowsStarted.Add(1, new KeyValuePair<string, object?>("workflow", "emergency_triage"));
        return new WorkflowStartResult(handle.Id, handle.ResultRunId ?? string.Empty, DateTimeOffset.UtcNow);
    }

    public async Task<WorkflowStartResult> StartAppointmentSchedulingAsync(AppointmentSchedulingInput input, string? workflowId = null, CancellationToken ct = default)
    {
        EnsureWorkflowVersionGate();
        var id = BuildWorkflowId($"scheduling-{input.PatientId:N}", workflowId);
        var handle = await client.StartWorkflowAsync(
            (AppointmentSchedulingWorkflow wf) => wf.RunAsync(input),
            new WorkflowOptions(id: id, taskQueue: options.TaskQueue)).ConfigureAwait(false);
        HopeMeters.WorkflowsStarted.Add(1, new KeyValuePair<string, object?>("workflow", "appointment_scheduling"));
        return new WorkflowStartResult(handle.Id, handle.ResultRunId ?? string.Empty, DateTimeOffset.UtcNow);
    }

    public async Task<WorkflowStartResult> StartMedicationReminderAsync(MedicationReminderInput input, string? workflowId = null, CancellationToken ct = default)
    {
        EnsureWorkflowVersionGate();
        var id = BuildWorkflowId($"reminder-{input.PatientId:N}", workflowId);
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
        EnsureWorkflowVersionGate();
        var id = BuildWorkflowId($"audit-{input.ReportType}-{DateTimeOffset.UtcNow:yyyyMMdd}", workflowId);
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

    private string BuildWorkflowId(string prefix, string? explicitId)
    {
        var version = GetNormalizedWorkflowVersion();

        if (!string.IsNullOrWhiteSpace(explicitId))
        {
            var explicitVersion = TryExtractWorkflowVersion(explicitId);
            if (!string.IsNullOrWhiteSpace(explicitVersion)
                && !string.Equals(explicitVersion, version, StringComparison.OrdinalIgnoreCase)
                && !IsExplicitVersionAllowed(explicitVersion))
            {
                throw new InvalidOperationException(
                    $"WorkflowId version mismatch blocked by rollout policy: explicit='{explicitVersion}' configured='{version}'.");
            }

            return string.IsNullOrWhiteSpace(explicitVersion)
                ? $"{explicitId}-wv{version}"
                : explicitId;
        }

        return $"{prefix}-{Guid.CreateVersion7():N}-wv{version}";
    }

    private void EnsureWorkflowVersionGate()
    {
        if (!options.EnforceWorkflowVersionGate)
            return;

        var version = GetNormalizedWorkflowVersion();
        var allowed = GetAllowedVersionsForNow();

        if (!allowed.Contains(version, StringComparer.OrdinalIgnoreCase))
        {
            throw new InvalidOperationException(
                $"Temporal workflow version '{version}' is not in allowed set [{string.Join(",", allowed)}].");
        }
    }

    private string GetNormalizedWorkflowVersion()
        => string.IsNullOrWhiteSpace(options.WorkflowVersion)
            ? "v1"
            : options.WorkflowVersion.Trim();

    private static string? TryExtractWorkflowVersion(string workflowId)
    {
        var marker = workflowId.LastIndexOf("-wv", StringComparison.OrdinalIgnoreCase);
        if (marker < 0)
            return null;

        var candidate = workflowId[(marker + 3)..].Trim();
        return string.IsNullOrWhiteSpace(candidate) ? null : candidate;
    }

    private bool IsExplicitVersionAllowed(string explicitVersion)
    {
        var current = GetNormalizedWorkflowVersion();
        if (string.Equals(explicitVersion, current, StringComparison.OrdinalIgnoreCase))
            return true;

        if (!options.EnforceWorkflowVersionGate)
            return true;

        if (IsAfterCutover()
            && options.AutoBlockPreviousVersionsAfterCutover)
        {
            return false;
        }

        var allowed = GetAllowedVersionsForNow();
        return allowed.Contains(explicitVersion, StringComparer.OrdinalIgnoreCase);
    }

    private string[] GetAllowedVersionsForNow()
    {
        var configured = options.AllowedWorkflowVersions
            .Where(v => !string.IsNullOrWhiteSpace(v))
            .Select(v => v.Trim())
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();

        if (configured.Count == 0)
            configured.Add(GetNormalizedWorkflowVersion());

        if (!options.EnableCanaryMultiVersionRollout)
            return configured.ToArray();

        if (IsAfterCutover())
            return configured.ToArray();

        foreach (var canary in options.CanaryAllowedWorkflowVersions)
        {
            if (!string.IsNullOrWhiteSpace(canary)
                && !configured.Contains(canary, StringComparer.OrdinalIgnoreCase))
            {
                configured.Add(canary.Trim());
            }
        }

        return configured.ToArray();
    }

    private bool IsAfterCutover()
        => options.CutoverAtUtc.HasValue && DateTimeOffset.UtcNow >= options.CutoverAtUtc.Value;
}
