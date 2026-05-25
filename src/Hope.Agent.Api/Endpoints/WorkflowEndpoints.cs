using System.Security.Claims;
using Hope.Agent.Application.Workflows;
using Microsoft.AspNetCore.Mvc;

namespace Hope.Agent.Api.Endpoints;

public static class WorkflowEndpoints
{
    public static IEndpointRouteBuilder MapWorkflowEndpoints(this IEndpointRouteBuilder app)
    {
        var grp = app.MapGroup("/v1/workflows").RequireAuthorization().WithTags("Workflows");

        grp.MapPost("/admissions", async (
            [FromBody] StartAdmissionRequest req,
            [FromServices] IWorkflowDispatcher dispatcher,
            ClaimsPrincipal user,
            CancellationToken ct) =>
        {
            var userId = ResolveUserId(user);
            var input = new PatientAdmissionInput(
                PatientId: req.PatientId,
                UserId: userId,
                ReasonForAdmission: req.Reason,
                InsuranceProvider: req.InsuranceProvider,
                PreferredDoctorId: req.PreferredDoctorId,
                Priority: req.Priority);
            var res = await dispatcher.StartPatientAdmissionAsync(input, req.WorkflowId, ct);
            return Results.Accepted($"/v1/workflows/{res.WorkflowId}", res);
        });

        grp.MapPost("/triage", async (
            [FromBody] StartTriageRequest req,
            [FromServices] IWorkflowDispatcher dispatcher,
            ClaimsPrincipal user,
            CancellationToken ct) =>
        {
            var userId = ResolveUserId(user);
            var input = new EmergencyTriageInput(
                PatientId: req.PatientId,
                UserId: userId,
                Symptoms: req.Symptoms,
                Location: req.Location);
            var res = await dispatcher.StartEmergencyTriageAsync(input, req.WorkflowId, ct);
            return Results.Accepted($"/v1/workflows/{res.WorkflowId}", res);
        });

        grp.MapPost("/{workflowId}/signal", async (
            string workflowId,
            [FromBody] SignalRequest req,
            [FromServices] IWorkflowDispatcher dispatcher,
            ClaimsPrincipal user,
            CancellationToken ct) =>
        {
            var decision = new ApprovalDecision(req.Step, req.Approved, req.Reason, ResolveUserId(user));
            await dispatcher.SignalApprovalAsync(workflowId, decision, ct);
            return Results.NoContent();
        });

        grp.MapGet("/{workflowId}", async (
            string workflowId,
            [FromServices] IWorkflowDispatcher dispatcher,
            CancellationToken ct) =>
        {
            var status = await dispatcher.GetStatusAsync(workflowId, ct);
            return Results.Ok(status);
        });

        grp.MapPost("/{workflowId}/cancel", async (
            string workflowId,
            [FromBody] CancelRequest req,
            [FromServices] IWorkflowDispatcher dispatcher,
            CancellationToken ct) =>
        {
            await dispatcher.CancelAsync(workflowId, req.Reason, ct);
            return Results.NoContent();
        });

        // ── Appointment Scheduling ────────────────────────────────────────────
        grp.MapPost("/scheduling", async (
            [FromBody] StartSchedulingRequest req,
            [FromServices] IWorkflowDispatcher dispatcher,
            ClaimsPrincipal user,
            CancellationToken ct) =>
        {
            var userId = ResolveUserId(user);
            var input = new AppointmentSchedulingInput(
                PatientId: req.PatientId,
                UserId: userId,
                ChiefComplaint: req.ChiefComplaint,
                Urgency: req.Urgency,
                PreferredDoctorId: req.PreferredDoctorId,
                PreferredTime: req.PreferredTime,
                InsuranceCardNumber: req.InsuranceCardNumber);
            var res = await dispatcher.StartAppointmentSchedulingAsync(input, req.WorkflowId, ct);
            return Results.Accepted($"/v1/workflows/{res.WorkflowId}", res);
        });

        // ── Medication Reminder ───────────────────────────────────────────────
        grp.MapPost("/reminders", async (
            [FromBody] StartReminderRequest req,
            [FromServices] IWorkflowDispatcher dispatcher,
            ClaimsPrincipal user,
            CancellationToken ct) =>
        {
            var userId = ResolveUserId(user);
            var input = new MedicationReminderInput(
                PatientId: req.PatientId,
                UserId: userId,
                MedicationName: req.MedicationName,
                Dosage: req.Dosage,
                Frequency: req.Frequency,
                StartAt: req.StartAt ?? DateTimeOffset.UtcNow.AddHours(1),
                DurationDays: req.DurationDays,
                PreferredChannel: req.PreferredChannel,
                AdherenceRiskScore: req.AdherenceRiskScore);
            var res = await dispatcher.StartMedicationReminderAsync(input, req.WorkflowId, ct);
            return Results.Accepted($"/v1/workflows/{res.WorkflowId}", res);
        });

        grp.MapPost("/reminders/{workflowId}/confirm", async (
            string workflowId,
            [FromBody] ReminderConfirmRequest req,
            [FromServices] IWorkflowDispatcher dispatcher,
            CancellationToken ct) =>
        {
            var confirmation = new ReminderConfirmation(workflowId, req.Confirmed, req.Note);
            await dispatcher.SignalReminderConfirmationAsync(workflowId, confirmation, ct);
            return Results.NoContent();
        });

        // ── Audit Report ──────────────────────────────────────────────────────
        grp.MapPost("/audit", async (
            [FromBody] StartAuditRequest req,
            [FromServices] IWorkflowDispatcher dispatcher,
            ClaimsPrincipal user,
            CancellationToken ct) =>
        {
            var input = new AuditReportInput(
                RequestedBy: ResolveUserId(user),
                ReportType: req.ReportType,
                PeriodStart: req.PeriodStart,
                PeriodEnd: req.PeriodEnd,
                ExportFormat: req.ExportFormat);
            var res = await dispatcher.StartAuditReportAsync(input, req.WorkflowId, ct);
            return Results.Accepted($"/v1/workflows/{res.WorkflowId}", res);
        });

        return app;
    }

    private static Guid ResolveUserId(ClaimsPrincipal user)
    {
        var sub = user.FindFirstValue(ClaimTypes.NameIdentifier) ?? user.FindFirstValue("sub");
        return Guid.TryParse(sub, out var id) ? id : Guid.Empty;
    }
}

public sealed record StartAdmissionRequest(
    Guid PatientId,
    string Reason,
    string? InsuranceProvider = null,
    string? PreferredDoctorId = null,
    int Priority = 5,
    string? WorkflowId = null);

public sealed record StartTriageRequest(
    Guid PatientId,
    string Symptoms,
    string? Location = null,
    string? WorkflowId = null);

public sealed record SignalRequest(string Step, bool Approved, string? Reason);

public sealed record CancelRequest(string Reason);

public sealed record StartSchedulingRequest(
    Guid PatientId,
    string ChiefComplaint,
    string Urgency = "normal",
    string? PreferredDoctorId = null,
    string? PreferredTime = null,
    string? InsuranceCardNumber = null,
    string? WorkflowId = null);

public sealed record StartReminderRequest(
    Guid PatientId,
    string MedicationName,
    string Dosage,
    string Frequency,
    int DurationDays,
    DateTimeOffset? StartAt = null,
    string PreferredChannel = "zalo",
    int AdherenceRiskScore = 30,
    string? WorkflowId = null);

public sealed record ReminderConfirmRequest(bool Confirmed, string? Note = null);

public sealed record StartAuditRequest(
    string ReportType,
    DateTimeOffset PeriodStart,
    DateTimeOffset PeriodEnd,
    string ExportFormat = "json",
    string? WorkflowId = null);
