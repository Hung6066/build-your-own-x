using System.Text.Json;
using Hope.Agent.Application.Autonomy;
using Hope.Agent.Application.Abstractions;
using Hope.Agent.Application.Security;
using Hope.Agent.Application.Tools;
using Hope.Agent.Application.Governance;
using Hope.Agent.Domain.Autonomy;
using Hope.Agent.Infrastructure.Persistence;
using Hope.Agent.Shared;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace Hope.Agent.Infrastructure.Autonomy;

internal sealed class AutonomyDecisionService(
    IAgentDecisionStore decisions,
    IOptionsMonitor<AutonomyOptions> options) : IAutonomyDecisionService
{
    public AutonomyEvaluationResult Evaluate(AutonomyEvaluationRequest request)
    {
        var opts = options.CurrentValue;
        if (!opts.Enabled)
            return new(AutonomyRiskLevel.Low, AutonomyPolicyDecision.SuggestOnly, AgentDecisionStatus.Suggested, "autonomy_disabled");

        var risk = ClassifyRisk(request.Intent, request.ToolName, request.Input, request.ArgumentsJson, opts);
        var forced = ForcedDecision(request, risk, opts);
        if (forced is not null) return forced;

        if (string.Equals(opts.Mode, "AutoExecute", StringComparison.OrdinalIgnoreCase)
            && risk <= opts.AutoExecuteMaxRisk
            && request.Confidence >= opts.MinConfidenceForAutoExecute)
        {
            return new(risk, AutonomyPolicyDecision.AutoExecute, AgentDecisionStatus.Queued, "low_risk_high_confidence");
        }

        if (risk is AutonomyRiskLevel.Low)
            return new(risk, AutonomyPolicyDecision.SuggestOnly, AgentDecisionStatus.Suggested, "low_risk_suggest_only");

        return new(risk, AutonomyPolicyDecision.RequireApproval, AgentDecisionStatus.RequiresApproval, "risk_requires_approval");
    }

    public Task<AgentDecision> RecordDecisionAsync(AgentDecisionWrite decision, CancellationToken ct)
        => decisions.AddAsync(decision, ct);

    private static AutonomyEvaluationResult? ForcedDecision(
        AutonomyEvaluationRequest request,
        AutonomyRiskLevel risk,
        AutonomyOptions opts)
    {
        var text = $"{request.Intent} {request.Input} {request.ToolName} {request.ArgumentsJson}".ToLowerInvariant();
        if (opts.RequireApprovalForMedicationChange &&
            ContainsAny(text, "change dose", "increase dose", "decrease dose", "đổi liều", "tăng liều", "giảm liều", "ngưng thuốc", "dừng thuốc"))
        {
            return new(AutonomyRiskLevel.Critical, AutonomyPolicyDecision.RequireApproval, AgentDecisionStatus.RequiresApproval, "medication_change_requires_approval");
        }

        if (opts.RequireApprovalForPhiExport &&
            ContainsAny(text, "export_phi", "phi export", "xuất phi", "export_audit_report"))
        {
            return new(AutonomyRiskLevel.Critical, AutonomyPolicyDecision.RequireApproval, AgentDecisionStatus.RequiresApproval, "phi_or_report_export_requires_approval");
        }

        if (ContainsAny(text, "severity\":4", "severity\":5", "level\":4", "level\":5", "đột quỵ", "stroke", "nhồi máu", "sepsis", "cấp cứu"))
        {
            return new(AutonomyRiskLevel.Critical, AutonomyPolicyDecision.RequireApproval, AgentDecisionStatus.RequiresApproval, "emergency_requires_human_review");
        }

        return risk == AutonomyRiskLevel.Critical
            ? new(risk, AutonomyPolicyDecision.RequireApproval, AgentDecisionStatus.RequiresApproval, "critical_risk_requires_approval")
            : null;
    }

    private static AutonomyRiskLevel ClassifyRisk(string intent, string? toolName, string input, string? argumentsJson, AutonomyOptions opts)
    {
        var tool = toolName ?? string.Empty;
        if (ContainsAny(tool, "patient_lookup", "search_clinical_guidelines", "get_doctor_slots", "get_medication_schedule", "collect_audit_logs", "detect_audit_anomalies"))
            return AutonomyRiskLevel.Low;
        if (ContainsAny(tool, "update_reminder_status", "persist_medical_summary"))
            return AutonomyRiskLevel.Low;
        if (ContainsAny(tool, "create_reminder_record", "schedule_appointment"))
            return AutonomyRiskLevel.Medium;
        if (ContainsAny(tool, "commit_booking", "throttle_notifications"))
            return AutonomyRiskLevel.High;
        if (ContainsAny(tool, "export_audit_report"))
            return AutonomyRiskLevel.Critical;

        var text = $"{intent} {input} {argumentsJson}".ToLowerInvariant();
        if (ContainsAny(text, "diagnosis final", "chẩn đoán cuối", "medication change", "đổi liều", "xuất dữ liệu", "phi"))
            return AutonomyRiskLevel.Critical;
        if (ContainsAny(text, "appointment", "reminder", "follow-up", "tái khám", "nhắc thuốc"))
            return AutonomyRiskLevel.Medium;
        if (ContainsAny(text, "audit", "compliance", "medical_summary", "summary", "tóm tắt"))
            return AutonomyRiskLevel.Low;
        return AutonomyRiskLevel.High;
    }

    private static bool ContainsAny(string text, params string[] terms)
        => terms.Any(term => text.Contains(term, StringComparison.OrdinalIgnoreCase));
}

internal sealed class PatientTimelineService(IDbContextFactory<AgentDbContext> dbFactory) : IPatientTimelineService
{
    public async Task<PatientTimeline> GetTimelineAsync(Guid patientId, int take, CancellationToken ct)
    {
        await using var db = await dbFactory.CreateDbContextAsync(ct).ConfigureAwait(false);
        var items = new List<PatientTimelineItem>();

        items.AddRange(await db.Memories.AsNoTracking()
            .Where(x => x.UserId == patientId)
            .OrderByDescending(x => x.CreatedAt)
            .Take(take)
            .Select(x => new PatientTimelineItem("agent_memories", x.Kind.ToString(), x.CreatedAt, x.Content, x.Id.ToString()))
            .ToListAsync(ct).ConfigureAwait(false));

        items.AddRange(await db.MedicalSummaries.AsNoTracking()
            .Where(x => x.PatientId == patientId)
            .OrderByDescending(x => x.CreatedAt)
            .Take(take)
            .Select(x => new PatientTimelineItem("medical_summaries", x.SummaryType, x.CreatedAt, x.SummaryText, x.SummaryId))
            .ToListAsync(ct).ConfigureAwait(false));

        items.AddRange(await db.ReminderRecords.AsNoTracking()
            .Where(x => x.PatientId == patientId)
            .OrderByDescending(x => x.UpdatedAt)
            .Take(take)
            .Select(x => new PatientTimelineItem("reminder_records", x.Status, x.UpdatedAt, x.MedicationName + " " + x.Frequency, x.ReminderId))
            .ToListAsync(ct).ConfigureAwait(false));

        items.AddRange(await db.AppointmentBookings.AsNoTracking()
            .Where(x => x.PatientId == patientId)
            .OrderByDescending(x => x.ConfirmedAt)
            .Take(take)
            .Select(x => new PatientTimelineItem("appointment_bookings", x.Status, x.ConfirmedAt, x.Reason ?? string.Empty, x.BookingId))
            .ToListAsync(ct).ConfigureAwait(false));

        var patientText = patientId.ToString();
        items.AddRange(await db.AuditEvents.AsNoTracking()
            .Where(x => x.PatientId == patientText || x.ResourceId == patientText)
            .OrderByDescending(x => x.OccurredAt)
            .Take(take)
            .Select(x => new PatientTimelineItem("audit_logs", x.Action, x.OccurredAt, (x.ResourceType ?? string.Empty) + ":" + (x.ResourceId ?? string.Empty), x.Id.ToString()))
            .ToListAsync(ct).ConfigureAwait(false));

        return new PatientTimeline(patientId, items.OrderByDescending(x => x.OccurredAt).Take(take).ToList());
    }
}

internal sealed class AgentSuggestionService(
    IPatientTimelineService timeline,
    IAutonomyDecisionService autonomy,
    IAgentDecisionStore decisions,
    IAutonomousActionStore actions,
    IAutonomySafetyBudget safetyBudget,
    IAutonomyLevel5ControlService level5Control,
    IOptionsMonitor<AgentVersionOptions> versionOptions) : IAgentSuggestionService
{
    public async Task<AgentSuggestionResult> SuggestAsync(Guid patientId, Guid userId, string goal, string? correlationId, CancellationToken ct)
    {
        var tl = await timeline.GetTimelineAsync(patientId, 50, ct).ConfigureAwait(false);
        var latestReminder = tl.Items.FirstOrDefault(x => x.Source == "reminder_records");
        var hasReminder = latestReminder is not null;
        var hasDiabetes = tl.Items.Any(x => x.Summary.Contains("T2DM", StringComparison.OrdinalIgnoreCase)
            || x.Summary.Contains("đái tháo đường", StringComparison.OrdinalIgnoreCase)
            || x.Summary.Contains("Metformin", StringComparison.OrdinalIgnoreCase));

        var tool = hasReminder ? "update_reminder_status" : "create_reminder_record";
        var args = hasReminder
            ? JsonSerializer.Serialize(new
            {
                reminder_id = latestReminder!.ReferenceId,
                status = "follow_up_suggested",
            })
            : JsonSerializer.Serialize(new
            {
                patient_id = patientId,
                reminder_type = "follow_up",
                medication_name = hasDiabetes ? "Metformin" : "theo hồ sơ",
                frequency = "once_daily",
                duration_days = 30,
                preferred_channel = "zalo",
                status = "suggested",
            });

        var confidence = await level5Control.CalibrateConfidenceAsync(tool, hasDiabetes || hasReminder ? 0.91 : 0.74, ct).ConfigureAwait(false);
        var intent = correlationId?.StartsWith("daily-autonomy-review:", StringComparison.OrdinalIgnoreCase) == true
            ? "daily_autonomy_review"
            : "follow_up_suggestion";
        var eval = autonomy.Evaluate(new AutonomyEvaluationRequest(
            userId, patientId, null, intent, null, goal, tool, args, confidence, correlationId));

        var decision = await decisions.AddAsync(new AgentDecisionWrite(
            userId,
            patientId,
            null,
            intent,
            "autonomy",
            Truncate(goal, 512),
            JsonSerializer.Serialize(tl.Items.Take(10).Select(x => new { x.Source, x.ReferenceId })),
            JsonSerializer.Serialize(new
            {
                timeline = tl.Items.Take(10),
                versionFingerprint = versionOptions.CurrentValue,
            }),
            JsonSerializer.Serialize(new { tool, arguments = JsonSerializer.Deserialize<JsonElement>(args) }),
            eval.RiskLevel,
            confidence,
            eval.PolicyDecision,
            eval.DecisionStatus,
            eval.Reason,
            correlationId), ct).ConfigureAwait(false);

        if (eval.RiskLevel >= AutonomyRiskLevel.High)
        {
            var review = await level5Control.ReviewAsync(
                decision.DecisionId,
                eval.RiskLevel,
                goal,
                JsonSerializer.Serialize(new { tool, arguments = JsonSerializer.Deserialize<JsonElement>(args) }),
                correlationId,
                ct).ConfigureAwait(false);
            if (!review.Passed)
            {
                await decisions.UpdateStatusAsync(decision.DecisionId, AgentDecisionStatus.RequiresApproval, $"second_review:{review.Notes}", ct).ConfigureAwait(false);
                eval = eval with { PolicyDecision = AutonomyPolicyDecision.RequireApproval, DecisionStatus = AgentDecisionStatus.RequiresApproval, Reason = $"second_review:{review.Notes}" };
            }
        }

        if (eval.PolicyDecision is AutonomyPolicyDecision.AutoExecute or AutonomyPolicyDecision.RequireApproval)
        {
            var budget = await safetyBudget.CheckAsync(patientId, tool, ct).ConfigureAwait(false);
            if (budget.Allowed)
            {
                await actions.AddAsync(new AutonomousActionWrite(
                    decision.DecisionId,
                    tool,
                    args,
                    eval.RiskLevel,
                    confidence,
                    eval.PolicyDecision == AutonomyPolicyDecision.AutoExecute ? AutonomousActionStatus.Approved : AutonomousActionStatus.Pending,
                    DateTimeOffset.UtcNow,
                    correlationId,
                    IdempotencyKey: $"{decision.DecisionId}:{tool}",
                    QueueBackend: "Temporal/Kafka"), ct).ConfigureAwait(false);
            }
            else
            {
                await decisions.UpdateStatusAsync(decision.DecisionId, AgentDecisionStatus.RequiresApproval, budget.Reason, ct).ConfigureAwait(false);
            }
        }

        var suggestion = new AgentSuggestion(
            "follow_up_reminder",
            hasDiabetes
                ? "Bệnh nhân có dữ liệu T2DM/Metformin; nên tạo nhắc tái khám/nhắc thuốc theo kế hoạch hiện có."
                : "Dữ liệu cũ chưa đủ mạnh; chỉ nên tạo gợi ý follow-up để nhân viên y tế xem lại.",
            eval.RiskLevel,
            confidence,
            eval.PolicyDecision,
            new { tool, arguments = JsonSerializer.Deserialize<JsonElement>(args) });

        return new AgentSuggestionResult(decision.DecisionId, patientId, [suggestion]);
    }

    private static string Truncate(string value, int max) => value.Length <= max ? value : value[..max];
}

internal sealed class AutonomousActionExecutor(
    IAutonomousActionStore actions,
    IAgentDecisionStore decisions,
    IToolRegistry tools,
    IToolExecutor toolExecutor,
    IAutonomyOutcomeVerifier outcomeVerifier,
    IAutonomyLevel5ControlService level5Control,
    IOptionsMonitor<AutonomyLevel5Options> level5,
    IAuditSink audit,
    IClock clock,
    ILogger<AutonomousActionExecutor> log) : IAutonomousActionExecutor
{
    public async Task ExecuteDueAsync(CancellationToken ct)
    {
        var due = await actions.DueAsync(clock.UtcNow, 100, ct).ConfigureAwait(false);
        foreach (var action in due)
        {
            if (action.RiskLevel > AutonomyRiskLevel.Low && action.Status != AutonomousActionStatus.Approved)
                continue;

            if (level5.CurrentValue.RequireEvalGateForAutoExecute)
            {
                var readiness = await level5Control.GetReadinessAsync(ct).ConfigureAwait(false);
                if (!readiness.Ready)
                {
                    action.Error = $"level5_not_ready:{readiness.Reason}";
                    action.ScheduledFor = clock.UtcNow.AddMinutes(15);
                    await actions.UpdateAsync(action, ct).ConfigureAwait(false);
                    continue;
                }
            }

            action.Status = AutonomousActionStatus.Executing;
            action.AttemptCount++;
            await actions.UpdateAsync(action, ct).ConfigureAwait(false);

            try
            {
                var tool = tools.Find(action.ToolName) ?? throw new InvalidOperationException($"Tool '{action.ToolName}' not found.");
                var ctx = new ToolInvocationContext(
                    Guid.Empty,
                    Guid.Empty,
                    action.CorrelationId ?? action.ActionId,
                    ["system"],
                    action.TenantId,
                    action.IdempotencyKey ?? action.ActionId);
                var output = await toolExecutor.InvokeAsync(tool, action.ArgumentsJson, ctx, ct).ConfigureAwait(false) ?? "{}";
                var verification = level5.CurrentValue.RequireOutcomeVerification
                    ? outcomeVerifier.Verify(action.ToolName, action.ArgumentsJson, output)
                    : new AutonomyOutcomeVerification(true, "verification_disabled");
                if (!verification.Verified)
                    throw new InvalidOperationException($"outcome_verification_failed:{verification.Reason}");
                action.ResultJson = output;
                action.ExecutedAt = clock.UtcNow;
                action.Status = AutonomousActionStatus.Succeeded;
                await actions.UpdateAsync(action, ct).ConfigureAwait(false);
                await decisions.UpdateStatusAsync(action.DecisionId, AgentDecisionStatus.AutoExecuted, "action_succeeded", ct).ConfigureAwait(false);
                await audit.WriteAsync(new Hope.Agent.Domain.Audit.AuditEvent
                {
                    Id = Guid.CreateVersion7(),
                    OccurredAt = clock.UtcNow,
                    Actor = "autonomous_action_worker",
                    Action = "autonomy.action.succeeded",
                    ResourceType = "autonomous_action",
                    ResourceId = action.ActionId,
                    CorrelationId = action.CorrelationId,
                    PayloadJson = JsonSerializer.Serialize(new { action.ToolName, action.DecisionId }),
                }, ct).ConfigureAwait(false);
            }
            catch (Exception ex)
            {
                log.LogWarning(ex, "Autonomous action failed: {ActionId}", action.ActionId);
                action.Error = ex.Message;
                action.Status = action.AttemptCount >= 3 ? AutonomousActionStatus.Failed : AutonomousActionStatus.Approved;
                action.ScheduledFor = clock.UtcNow.AddMinutes(Math.Min(action.AttemptCount * 2, 10));
                await actions.UpdateAsync(action, ct).ConfigureAwait(false);
                if (action.Status == AutonomousActionStatus.Failed)
                {
                    await decisions.UpdateStatusAsync(action.DecisionId, AgentDecisionStatus.Failed, ex.Message, ct).ConfigureAwait(false);
                    if (level5.CurrentValue.EnableCompensation)
                        await level5Control.CreateCompensationAsync(action, ex.Message, ct).ConfigureAwait(false);
                }
            }
        }
    }
}

internal sealed class AutonomyLevel5ControlService(
    IDbContextFactory<AgentDbContext> dbFactory,
    IToolRegistry tools,
    IOptionsMonitor<AutonomyLevel5Options> options,
    IClock clock) : IAutonomyLevel5ControlService
{
    public async Task<AutonomyEvalGateResult> RunEvalGateAsync(string suiteName, string? correlationId, CancellationToken ct)
    {
        await using var db = await dbFactory.CreateDbContextAsync(ct).ConfigureAwait(false);
        var now = clock.UtcNow;
        var since = now.AddDays(-Math.Max(options.CurrentValue.ConfidenceCalibrationWindowDays, 1));
        var actions = await db.AutonomousActions.AsNoTracking()
            .Where(x => x.CreatedAt >= since && (x.Status == AutonomousActionStatus.Succeeded || x.Status == AutonomousActionStatus.Failed))
            .ToListAsync(ct)
            .ConfigureAwait(false);
        var total = actions.Count;
        var succeeded = actions.Count(x => x.Status == AutonomousActionStatus.Succeeded);
        var passRate = total == 0 ? 1.0 : (double)succeeded / total;
        var failedCritical = actions.Count(x => x.RiskLevel >= AutonomyRiskLevel.High && x.Status == AutonomousActionStatus.Failed);
        var passed = passRate >= options.CurrentValue.MinEvalGatePassRate && failedCritical == 0;
        var metrics = new { total, succeeded, failed = total - succeeded, failedCritical, passRate };
        var entity = new AutonomyEvalGateRun
        {
            Id = Guid.CreateVersion7(),
            GateId = $"GATE-{now:yyyyMMdd}-{Guid.NewGuid().ToString("N")[..10].ToUpperInvariant()}",
            SuiteName = string.IsNullOrWhiteSpace(suiteName) ? "level5_operational" : suiteName,
            Passed = passed,
            PassRate = passRate,
            MetricsJson = JsonSerializer.Serialize(metrics),
            Reason = passed ? "eval_gate_passed" : "eval_gate_failed",
            CreatedAt = now,
            CorrelationId = correlationId,
        };
        await db.AutonomyEvalGateRuns.AddAsync(entity, ct).ConfigureAwait(false);
        await db.SaveChangesAsync(ct).ConfigureAwait(false);
        return new(entity.GateId, passed, passRate, entity.Reason, metrics);
    }

    public async Task<AutonomyDriftResult> DetectDriftAsync(string? correlationId, CancellationToken ct)
    {
        await using var db = await dbFactory.CreateDbContextAsync(ct).ConfigureAwait(false);
        var now = clock.UtcNow;
        var currentSince = now.AddHours(-1);
        var baselineSince = now.AddDays(-1);
        var current = await db.AutonomousActions.AsNoTracking()
            .Where(x => x.CreatedAt >= currentSince && (x.Status == AutonomousActionStatus.Succeeded || x.Status == AutonomousActionStatus.Failed))
            .ToListAsync(ct).ConfigureAwait(false);
        var baseline = await db.AutonomousActions.AsNoTracking()
            .Where(x => x.CreatedAt >= baselineSince && x.CreatedAt < currentSince && (x.Status == AutonomousActionStatus.Succeeded || x.Status == AutonomousActionStatus.Failed))
            .ToListAsync(ct).ConfigureAwait(false);

        static double FailureRate(IReadOnlyCollection<AutonomousAction> rows)
            => rows.Count == 0 ? 0 : (double)rows.Count(x => x.Status == AutonomousActionStatus.Failed) / rows.Count;

        var currentRate = FailureRate(current);
        var baselineRate = FailureRate(baseline);
        var score = Math.Clamp(currentRate - baselineRate, 0, 1);
        var severity = score >= options.CurrentValue.MaxAllowedDriftScore * 2 ? AutonomyDriftSeverity.Critical
            : score >= options.CurrentValue.MaxAllowedDriftScore ? AutonomyDriftSeverity.Warning
            : AutonomyDriftSeverity.Info;
        var entity = new AutonomyDriftSignal
        {
            Id = Guid.CreateVersion7(),
            SignalId = $"DRIFT-{now:yyyyMMdd}-{Guid.NewGuid().ToString("N")[..10].ToUpperInvariant()}",
            SignalType = "failure_rate_delta",
            Severity = severity,
            Score = score,
            BaselineJson = JsonSerializer.Serialize(new { count = baseline.Count, failureRate = baselineRate }),
            CurrentJson = JsonSerializer.Serialize(new { count = current.Count, failureRate = currentRate }),
            Status = severity == AutonomyDriftSeverity.Info ? AutonomyControlStatus.Passed : AutonomyControlStatus.Warning,
            CreatedAt = now,
            CorrelationId = correlationId,
        };
        await db.AutonomyDriftSignals.AddAsync(entity, ct).ConfigureAwait(false);
        await db.SaveChangesAsync(ct).ConfigureAwait(false);
        return new(entity.SignalId, severity, score, severity == AutonomyDriftSeverity.Info ? "no_material_drift" : "failure_rate_drift_detected");
    }

    public async Task<AutonomyReadinessStatus> GetReadinessAsync(CancellationToken ct)
    {
        await using var db = await dbFactory.CreateDbContextAsync(ct).ConfigureAwait(false);
        var now = clock.UtcNow;
        var lastGate = await db.AutonomyEvalGateRuns.AsNoTracking()
            .OrderByDescending(x => x.CreatedAt)
            .FirstOrDefaultAsync(ct)
            .ConfigureAwait(false);
        if (lastGate is null || lastGate.CreatedAt < now.AddHours(-24))
        {
            var gate = await RunEvalGateAsync("level5_operational_auto", "readiness", ct).ConfigureAwait(false);
            lastGate = await db.AutonomyEvalGateRuns.AsNoTracking().FirstAsync(x => x.GateId == gate.GateId, ct).ConfigureAwait(false);
        }

        var drift = await db.AutonomyDriftSignals.AsNoTracking()
            .Where(x => x.CreatedAt >= now.AddDays(-1))
            .OrderByDescending(x => x.CreatedAt)
            .FirstOrDefaultAsync(ct)
            .ConfigureAwait(false);
        var driftScore = drift?.Score ?? 0;
        var criticalSignals = await db.AutonomyDriftSignals.AsNoTracking()
            .CountAsync(x => x.CreatedAt >= now.AddDays(-1) && x.Severity == AutonomyDriftSeverity.Critical, ct)
            .ConfigureAwait(false);
        var ready = lastGate.Passed && lastGate.PassRate >= options.CurrentValue.MinEvalGatePassRate && driftScore <= options.CurrentValue.MaxAllowedDriftScore && criticalSignals == 0;
        var reason = ready ? "level5_ready" : $"level5_blocked:eval={lastGate.PassRate:0.00};drift={driftScore:0.00};critical={criticalSignals}";
        return new(ready, lastGate.PassRate, driftScore, criticalSignals, reason);
    }

    public async Task<double> CalibrateConfidenceAsync(string toolName, double baseConfidence, CancellationToken ct)
    {
        await using var db = await dbFactory.CreateDbContextAsync(ct).ConfigureAwait(false);
        var since = clock.UtcNow.AddDays(-Math.Max(options.CurrentValue.ConfidenceCalibrationWindowDays, 1));
        var recent = await db.AutonomousActions.AsNoTracking()
            .Where(x => x.ToolName == toolName && x.CreatedAt >= since && (x.Status == AutonomousActionStatus.Succeeded || x.Status == AutonomousActionStatus.Failed))
            .ToListAsync(ct).ConfigureAwait(false);
        if (recent.Count == 0) return Math.Clamp(baseConfidence, 0, 1);
        var successRate = (double)recent.Count(x => x.Status == AutonomousActionStatus.Succeeded) / recent.Count;
        var adjustment = (successRate - 0.9) * 0.25;
        return Math.Clamp(baseConfidence + adjustment, 0.5, 0.99);
    }

    public async Task<AutonomyReviewResult> ReviewAsync(string decisionId, AutonomyRiskLevel risk, string input, string? actionJson, string? correlationId, CancellationToken ct)
    {
        var text = $"{input} {actionJson}".ToLowerInvariant();
        var forbidden = ContainsAny(text, "đổi liều", "tăng liều", "giảm liều", "ngưng thuốc", "phi export", "export_phi", "cấp cứu", "diagnosis final");
        var threshold = options.CurrentValue.RequireSecondReviewForRiskAtLeast;
        var needsReview = risk >= threshold;
        var passed = !needsReview || (!forbidden && risk <= AutonomyRiskLevel.Medium);
        var verdict = passed ? AutonomyControlStatus.Passed : AutonomyControlStatus.Failed;
        var notes = passed ? "second_review_passed" : "second_review_requires_human";

        await using var db = await dbFactory.CreateDbContextAsync(ct).ConfigureAwait(false);
        foreach (var reviewer in new[] { "safety_reviewer", "clinical_guard_reviewer" })
        {
            await db.AutonomyReviewRecords.AddAsync(new AutonomyReviewRecord
            {
                Id = Guid.CreateVersion7(),
                ReviewId = $"REV-{DateTimeOffset.UtcNow:yyyyMMdd}-{Guid.NewGuid().ToString("N")[..10].ToUpperInvariant()}",
                DecisionId = decisionId,
                ReviewerProfile = reviewer,
                Verdict = verdict,
                Confidence = passed ? 0.88 : 0.95,
                Notes = notes,
                CreatedAt = clock.UtcNow,
                CorrelationId = correlationId,
            }, ct).ConfigureAwait(false);
        }
        await db.SaveChangesAsync(ct).ConfigureAwait(false);
        return new(passed, verdict, passed ? 0.88 : 0.95, notes);
    }

    public async Task<AutonomyCompensationResult> CreateCompensationAsync(AutonomousAction action, string reason, CancellationToken ct)
    {
        if (!options.CurrentValue.EnableCompensation)
            return new(null, false, false, "compensation_disabled");

        var plan = BuildCompensationPlan(action);
        if (plan is null)
            return new(null, false, false, "no_compensation_available");

        var (toolName, args) = plan.Value;
        var record = new AutonomyCompensationRecord
        {
            Id = Guid.CreateVersion7(),
            CompensationId = $"COMP-{DateTimeOffset.UtcNow:yyyyMMdd}-{Guid.NewGuid().ToString("N")[..10].ToUpperInvariant()}",
            ActionId = action.ActionId,
            ToolName = toolName,
            ArgumentsJson = args,
            Status = AutonomyControlStatus.Pending,
            CreatedAt = clock.UtcNow,
            Error = reason,
            CorrelationId = action.CorrelationId,
        };

        await using var db = await dbFactory.CreateDbContextAsync(ct).ConfigureAwait(false);
        await db.AutonomyCompensationRecords.AddAsync(record, ct).ConfigureAwait(false);
        await db.SaveChangesAsync(ct).ConfigureAwait(false);

        try
        {
            var tool = tools.Find(toolName) ?? throw new InvalidOperationException($"Compensation tool '{toolName}' not found.");
            var ctx = new ToolInvocationContext(Guid.Empty, Guid.Empty, action.CorrelationId ?? record.CompensationId, ["system"]);
            record.ResultJson = await tool.InvokeAsync(args, ctx, ct).ConfigureAwait(false) ?? "{}";
            record.Status = AutonomyControlStatus.Executed;
            record.ExecutedAt = clock.UtcNow;
            await db.SaveChangesAsync(ct).ConfigureAwait(false);
            return new(record.CompensationId, true, true, "compensation_executed");
        }
        catch (Exception ex)
        {
            record.Status = AutonomyControlStatus.Failed;
            record.Error = ex.Message;
            await db.SaveChangesAsync(ct).ConfigureAwait(false);
            return new(record.CompensationId, true, false, ex.Message);
        }
    }

    private static (string ToolName, string Args)? BuildCompensationPlan(AutonomousAction action)
    {
        if (!action.ToolName.Equals("update_reminder_status", StringComparison.OrdinalIgnoreCase))
            return null;
        try
        {
            using var doc = JsonDocument.Parse(action.ArgumentsJson);
            var reminderId = doc.RootElement.TryGetProperty("reminder_id", out var id) ? id.GetString() : null;
            if (string.IsNullOrWhiteSpace(reminderId)) return null;
            return ("update_reminder_status", JsonSerializer.Serialize(new { reminder_id = reminderId, status = "needs_review" }));
        }
        catch
        {
            return null;
        }
    }

    private static bool ContainsAny(string text, params string[] terms)
        => terms.Any(term => text.Contains(term, StringComparison.OrdinalIgnoreCase));
}

internal sealed class AutonomySafetyBudget(
    IDbContextFactory<AgentDbContext> dbFactory,
    IOptionsMonitor<AutonomyLevel5Options> options) : IAutonomySafetyBudget
{
    public async Task<AutonomyBudgetDecision> CheckAsync(Guid? patientId, string toolName, CancellationToken ct)
    {
        var opts = options.CurrentValue;
        if (!opts.Enabled)
            return new(true, "level5_budget_disabled");

        if (!opts.AllowClinicalCriticalAutonomy &&
            (toolName.Contains("export_audit_report", StringComparison.OrdinalIgnoreCase)
             || toolName.Contains("commit_booking", StringComparison.OrdinalIgnoreCase)))
            return new(false, "clinical_or_critical_autonomy_not_allowed");

        await using var db = await dbFactory.CreateDbContextAsync(ct).ConfigureAwait(false);
        var now = DateTimeOffset.UtcNow;
        var today = new DateTimeOffset(now.UtcDateTime.Date, TimeSpan.Zero);
        var totalToday = await db.AutonomousActions.AsNoTracking()
            .CountAsync(x => x.CreatedAt >= today, ct)
            .ConfigureAwait(false);
        if (totalToday >= opts.MaxTotalActionsPerDay)
            return new(false, "daily_total_action_budget_exceeded");

        var failureWindow = now.AddHours(-1);
        var recentFailures = await db.AutonomousActions.AsNoTracking()
            .CountAsync(x => x.CreatedAt >= failureWindow && x.Status == AutonomousActionStatus.Failed, ct)
            .ConfigureAwait(false);
        if (recentFailures >= opts.AutoPauseFailureThresholdPerHour)
            return new(false, "autonomy_auto_paused_due_to_recent_failures");

        if (patientId is { } pid)
        {
            var actions = await db.AutonomousActions.AsNoTracking()
                .Where(x => x.CreatedAt >= today)
                .Select(x => x.ArgumentsJson)
                .ToListAsync(ct)
                .ConfigureAwait(false);
            var patientCount = actions.Count(json => json.Contains(pid.ToString(), StringComparison.OrdinalIgnoreCase));
            if (patientCount >= opts.MaxActionsPerPatientPerDay)
                return new(false, "daily_patient_action_budget_exceeded");
        }

        return new(true, "budget_ok");
    }
}

internal sealed class AutonomyOutcomeVerifier : IAutonomyOutcomeVerifier
{
    public AutonomyOutcomeVerification Verify(string toolName, string argumentsJson, string resultJson)
    {
        try
        {
            using var resultDoc = JsonDocument.Parse(resultJson);
            if (resultDoc.RootElement.TryGetProperty("error", out var error))
                return new(false, error.GetString() ?? "tool_error");

            if (toolName.Equals("update_reminder_status", StringComparison.OrdinalIgnoreCase))
            {
                using var argsDoc = JsonDocument.Parse(argumentsJson);
                var expected = argsDoc.RootElement.TryGetProperty("status", out var expectedStatus)
                    ? expectedStatus.GetString()
                    : null;
                var actual = resultDoc.RootElement.TryGetProperty("status", out var actualStatus)
                    ? actualStatus.GetString()
                    : null;
                if (!string.IsNullOrWhiteSpace(expected) &&
                    !string.Equals(expected, actual, StringComparison.OrdinalIgnoreCase))
                    return new(false, "reminder_status_mismatch");
            }

            if (toolName.Equals("create_reminder_record", StringComparison.OrdinalIgnoreCase) &&
                !resultDoc.RootElement.TryGetProperty("reminder_id", out _))
                return new(false, "missing_reminder_id");

            return new(true, "verified");
        }
        catch (Exception ex)
        {
            return new(false, $"invalid_json:{ex.Message}");
        }
    }
}

internal sealed class AutonomyAgiLikeService(
    IDbContextFactory<AgentDbContext> dbFactory,
    IPatientTimelineService timeline,
    IAgentSuggestionService suggestions,
    IAutonomyGoalStore goals,
    IAutonomyReflectionStore reflections,
    IAutonomyLearningFactStore learningFacts,
    IOptionsMonitor<AutonomyAgiLikeOptions> options,
    ILogger<AutonomyAgiLikeService> log) : IAutonomyAgiLikeService
{
    public async Task<AutonomyAgiLikeRunResult> RunOnceAsync(Guid userId, bool force, CancellationToken ct)
    {
        var opts = options.CurrentValue;
        if (!opts.Enabled && !force)
            return new(0, 0, 0, 0, "disabled");

        var goalsCreated = 0;
        var suggestionsCreated = 0;
        var reflectionsCreated = await ReflectOnRecentActionsAsync(opts, ct).ConfigureAwait(false);
        var factsBefore = await CountLearningFactsAsync(ct).ConfigureAwait(false);

        foreach (var candidate in await SelectPatientCandidatesAsync(opts, ct).ConfigureAwait(false))
        {
            ct.ThrowIfCancellationRequested();
            try
            {
                var proposed = await ProposeGoalAsync(candidate, userId, opts, force, ct).ConfigureAwait(false);
                if (proposed is null) continue;

                var goal = await goals.AddAsync(proposed, ct).ConfigureAwait(false);
                goalsCreated++;

                var result = await suggestions.SuggestAsync(
                    candidate,
                    userId,
                    goal.Description,
                    goal.CorrelationId ?? $"agi-like:{DateTimeOffset.UtcNow:yyyyMMdd}:{candidate:N}",
                    ct).ConfigureAwait(false);
                await goals.UpdateStatusAsync(goal.GoalId, AutonomyGoalStatus.Queued, result.DecisionId, "suggestion_created", ct).ConfigureAwait(false);
                suggestionsCreated += result.Suggestions.Count;
            }
            catch (Exception ex)
            {
                log.LogWarning(ex, "AGI-like goal loop failed for patient {PatientId}", candidate);
            }
        }

        var factsAfter = await CountLearningFactsAsync(ct).ConfigureAwait(false);
        return new(goalsCreated, suggestionsCreated, reflectionsCreated, Math.Max(0, factsAfter - factsBefore), opts.Enabled ? "enabled" : "forced");
    }

    public async Task<AutonomyAgiLikeStatus> GetStatusAsync(CancellationToken ct)
    {
        await using var db = await dbFactory.CreateDbContextAsync(ct).ConfigureAwait(false);
        var now = DateTimeOffset.UtcNow;
        var today = new DateTimeOffset(now.UtcDateTime.Date, TimeSpan.Zero);
        var openGoals = await db.AutonomyGoals.AsNoTracking()
            .CountAsync(x => x.Status == AutonomyGoalStatus.Proposed || x.Status == AutonomyGoalStatus.Queued || x.Status == AutonomyGoalStatus.InProgress, ct)
            .ConfigureAwait(false);
        var completedGoalsToday = await db.AutonomyGoals.AsNoTracking()
            .CountAsync(x => x.CompletedAt >= today && x.Status == AutonomyGoalStatus.Completed, ct)
            .ConfigureAwait(false);
        var reflectionsToday = await db.AutonomyReflections.AsNoTracking()
            .CountAsync(x => x.CreatedAt >= today, ct)
            .ConfigureAwait(false);
        var facts = await db.AutonomyLearningFacts.AsNoTracking().CountAsync(ct).ConfigureAwait(false);
        var actionsSucceeded = await db.AutonomousActions.AsNoTracking()
            .CountAsync(x => x.CreatedAt >= today && x.Status == AutonomousActionStatus.Succeeded, ct)
            .ConfigureAwait(false);
        var actionsFailed = await db.AutonomousActions.AsNoTracking()
            .CountAsync(x => x.CreatedAt >= today && x.Status == AutonomousActionStatus.Failed, ct)
            .ConfigureAwait(false);
        return new(options.CurrentValue.Enabled, openGoals, completedGoalsToday, reflectionsToday, facts, actionsSucceeded, actionsFailed);
    }

    private async Task<AutonomyGoalWrite?> ProposeGoalAsync(Guid patientId, Guid userId, AutonomyAgiLikeOptions opts, bool force, CancellationToken ct)
    {
        var tl = await timeline.GetTimelineAsync(patientId, 40, ct).ConfigureAwait(false);
        if (tl.Items.Count < Math.Max(opts.MinEvidenceItems, 1))
            return null;

        await using var db = await dbFactory.CreateDbContextAsync(ct).ConfigureAwait(false);
        var today = new DateTimeOffset(DateTimeOffset.UtcNow.UtcDateTime.Date, TimeSpan.Zero);
        var alreadyOpen = await db.AutonomyGoals.AsNoTracking()
            .AnyAsync(x => x.PatientId == patientId && x.CreatedAt >= today
                && (x.Status == AutonomyGoalStatus.Proposed || x.Status == AutonomyGoalStatus.Queued || x.Status == AutonomyGoalStatus.InProgress), ct)
            .ConfigureAwait(false);
        if (alreadyOpen && !force) return null;

        var hasReminder = tl.Items.Any(x => x.Source == "reminder_records");
        var hasDiabetes = tl.Items.Any(x => x.Summary.Contains("T2DM", StringComparison.OrdinalIgnoreCase)
            || x.Summary.Contains("Metformin", StringComparison.OrdinalIgnoreCase)
            || x.Summary.Contains("đái tháo đường", StringComparison.OrdinalIgnoreCase));
        var hasRecentAppointment = tl.Items.Any(x => x.Source == "appointment_bookings" && x.OccurredAt >= DateTimeOffset.UtcNow.AddDays(-45));
        var missedSignal = tl.Items.Any(x => x.Summary.Contains("quên", StringComparison.OrdinalIgnoreCase)
            || x.Summary.Contains("miss", StringComparison.OrdinalIgnoreCase));

        var confidence = 0.68;
        var goalType = "care_gap_review";
        var description = "AGI-like care-gap review: phân tích dữ liệu cũ và tạo gợi ý follow-up an toàn.";
        if (hasDiabetes && hasReminder)
        {
            confidence = missedSignal ? 0.91 : 0.86;
            goalType = "adherence_follow_up";
            description = "AGI-like adherence follow-up: bệnh nhân có dữ liệu thuốc/nhắc thuốc; kiểm tra và đề xuất follow-up không thay đổi điều trị.";
        }
        else if (hasDiabetes && !hasReminder)
        {
            confidence = 0.82;
            goalType = "reminder_gap";
            description = "AGI-like reminder gap: bệnh nhân có dữ liệu T2DM/Metformin nhưng chưa có reminder gần đây; đề xuất tạo reminder draft.";
        }
        else if (!hasRecentAppointment)
        {
            confidence = 0.76;
            goalType = "appointment_follow_up_gap";
            description = "AGI-like appointment gap: chưa thấy lịch hẹn gần đây trong timeline; tạo gợi ý tái khám để nhân sự y tế xem lại.";
        }

        if (confidence < opts.MinGoalConfidence)
            return null;

        var evidence = JsonSerializer.Serialize(tl.Items.Take(12).Select(x => new
        {
            x.Source,
            x.Type,
            x.OccurredAt,
            x.ReferenceId,
            Summary = Truncate(x.Summary, 240),
        }));
        return new(
            patientId,
            userId,
            goalType,
            description,
            evidence,
            Math.Clamp(confidence + (missedSignal ? 0.05 : 0), 0, 1),
            confidence,
            opts.MaxGoalRisk,
            AutonomyGoalStatus.Proposed,
            null,
            "self_generated_from_patient_timeline",
            $"agi-like:{DateTimeOffset.UtcNow:yyyyMMdd}:{patientId:N}");
    }

    private async Task<IReadOnlyList<Guid>> SelectPatientCandidatesAsync(AutonomyAgiLikeOptions opts, CancellationToken ct)
    {
        await using var db = await dbFactory.CreateDbContextAsync(ct).ConfigureAwait(false);
        var since = DateTimeOffset.UtcNow.AddDays(-90);
        var ids = new HashSet<Guid>();
        foreach (var id in await db.ReminderRecords.AsNoTracking()
                     .Where(x => x.UpdatedAt >= since)
                     .OrderByDescending(x => x.UpdatedAt)
                     .Select(x => x.PatientId)
                     .Take(opts.MaxGoalsPerRun * 2)
                     .ToListAsync(ct).ConfigureAwait(false))
            ids.Add(id);
        foreach (var id in await db.MedicalSummaries.AsNoTracking()
                     .Where(x => x.PatientId != null && x.CreatedAt >= since)
                     .OrderByDescending(x => x.CreatedAt)
                     .Select(x => x.PatientId!.Value)
                     .Take(opts.MaxGoalsPerRun * 2)
                     .ToListAsync(ct).ConfigureAwait(false))
            ids.Add(id);
        foreach (var id in await db.Memories.AsNoTracking()
                     .Where(x => x.CreatedAt >= since)
                     .OrderByDescending(x => x.CreatedAt)
                     .Select(x => x.UserId)
                     .Take(opts.MaxGoalsPerRun * 2)
                     .ToListAsync(ct).ConfigureAwait(false))
            ids.Add(id);
        return ids.Take(Math.Max(opts.MaxGoalsPerRun, 1)).ToList();
    }

    private async Task<int> ReflectOnRecentActionsAsync(AutonomyAgiLikeOptions opts, CancellationToken ct)
    {
        await using var db = await dbFactory.CreateDbContextAsync(ct).ConfigureAwait(false);
        var since = DateTimeOffset.UtcNow.AddDays(-1);
        var reflectedActionIds = await db.AutonomyReflections.AsNoTracking()
            .Where(x => x.ActionId != null && x.CreatedAt >= since)
            .Select(x => x.ActionId!)
            .ToListAsync(ct)
            .ConfigureAwait(false);
        var reflected = reflectedActionIds.ToHashSet(StringComparer.OrdinalIgnoreCase);
        var actions = await db.AutonomousActions.AsNoTracking()
            .Where(x => x.CreatedAt >= since && (x.Status == AutonomousActionStatus.Succeeded || x.Status == AutonomousActionStatus.Failed))
            .OrderByDescending(x => x.CreatedAt)
            .Take(50)
            .ToListAsync(ct)
            .ConfigureAwait(false);

        var count = 0;
        foreach (var action in actions.Where(x => !reflected.Contains(x.ActionId)))
        {
            var patientId = TryReadPatientId(action.ArgumentsJson);
            var success = action.Status == AutonomousActionStatus.Succeeded;
            var lessons = JsonSerializer.Serialize(new[]
            {
                new
                {
                    action.ToolName,
                    action.RiskLevel,
                    action.AttemptCount,
                    outcome = success ? "succeeded" : "failed",
                    error = action.Error,
                },
            });
            await reflections.AddAsync(new AutonomyReflectionWrite(
                null,
                action.DecisionId,
                action.ActionId,
                patientId,
                success,
                success
                    ? $"Action {action.ToolName} succeeded and passed outcome verification."
                    : $"Action {action.ToolName} failed or did not pass outcome verification.",
                lessons,
                success ? 0.02 : -0.08,
                action.CorrelationId), ct).ConfigureAwait(false);
            count++;

            if (opts.AutoCreateLearningFacts)
            {
                var key = $"{action.ToolName}:{(success ? "success" : "failure")}";
                await learningFacts.UpsertAsync(new AutonomyLearningFactWrite(
                    success ? AutonomyLearningFactKind.OutcomePattern : AutonomyLearningFactKind.SafetySignal,
                    key,
                    JsonSerializer.Serialize(new
                    {
                        action.ToolName,
                        Success = success,
                        action.RiskLevel,
                        action.AttemptCount,
                        action.Error,
                    }),
                    success ? 0.82 : 0.9,
                    "autonomy_reflection"), ct).ConfigureAwait(false);
            }
        }

        return count;
    }

    private async Task<int> CountLearningFactsAsync(CancellationToken ct)
    {
        await using var db = await dbFactory.CreateDbContextAsync(ct).ConfigureAwait(false);
        return await db.AutonomyLearningFacts.AsNoTracking().CountAsync(ct).ConfigureAwait(false);
    }

    private static string Truncate(string value, int max) => value.Length <= max ? value : value[..max];

    private static Guid? TryReadPatientId(string json)
    {
        try
        {
            using var doc = JsonDocument.Parse(json);
            if (doc.RootElement.TryGetProperty("patient_id", out var value) && Guid.TryParse(value.GetString(), out var id))
                return id;
        }
        catch { }

        return null;
    }
}

internal sealed class AutonomousActionWorker(
    IServiceScopeFactory scopeFactory,
    IOptionsMonitor<AutonomyOptions> options,
    ILogger<AutonomousActionWorker> log) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                if (options.CurrentValue.Enabled)
                {
                    using var scope = scopeFactory.CreateScope();
                    var executor = scope.ServiceProvider.GetRequiredService<IAutonomousActionExecutor>();
                    await executor.ExecuteDueAsync(stoppingToken).ConfigureAwait(false);
                }
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested) { }
            catch (Exception ex)
            {
                log.LogWarning(ex, "Autonomous action worker pass failed");
            }

            await Task.Delay(TimeSpan.FromSeconds(30), stoppingToken).ConfigureAwait(false);
        }
    }
}

internal sealed class AutonomyDailyReviewService(
    IDbContextFactory<AgentDbContext> dbFactory,
    IAgentSuggestionService suggestions,
    IOptionsMonitor<AutonomyDailyReviewOptions> options,
    ILogger<AutonomyDailyReviewService> log) : IAutonomyDailyReviewService
{
    public async Task<int> RunOnceAsync(DateTimeOffset runAt, bool force, CancellationToken ct)
    {
        var opts = options.CurrentValue;
        if (!opts.Enabled && !force) return 0;

        await using var db = await dbFactory.CreateDbContextAsync(ct).ConfigureAwait(false);
        var runAtUtc = runAt.ToUniversalTime();
        var since = runAtUtc.AddDays(-Math.Max(opts.LookbackDays, 1));
        var today = new DateTimeOffset(runAtUtc.UtcDateTime.Date, TimeSpan.Zero);

        var alreadyReviewed = await db.AgentDecisions.AsNoTracking()
            .Where(x => x.Intent == "daily_autonomy_review" && x.CreatedAt >= today)
            .Select(x => x.PatientId)
            .Where(x => x != null)
            .ToListAsync(ct)
            .ConfigureAwait(false);
        var reviewed = alreadyReviewed.Select(x => x!.Value).ToHashSet();

        var patientIds = new HashSet<Guid>();
        var reminderPatients = await db.ReminderRecords.AsNoTracking()
            .Where(x => x.UpdatedAt >= since)
            .OrderByDescending(x => x.UpdatedAt)
            .Select(x => x.PatientId)
            .Take(opts.MaxPatientsPerRun * 2)
            .ToListAsync(ct)
            .ConfigureAwait(false);
        foreach (var id in reminderPatients) patientIds.Add(id);

        var summaryPatients = await db.MedicalSummaries.AsNoTracking()
            .Where(x => x.PatientId != null && x.CreatedAt >= since)
            .OrderByDescending(x => x.CreatedAt)
            .Select(x => x.PatientId!.Value)
            .Take(opts.MaxPatientsPerRun * 2)
            .ToListAsync(ct)
            .ConfigureAwait(false);
        foreach (var id in summaryPatients) patientIds.Add(id);

        var appointmentPatients = await db.AppointmentBookings.AsNoTracking()
            .Where(x => x.PatientId != null && x.ConfirmedAt >= since)
            .OrderByDescending(x => x.ConfirmedAt)
            .Select(x => x.PatientId!.Value)
            .Take(opts.MaxPatientsPerRun * 2)
            .ToListAsync(ct)
            .ConfigureAwait(false);
        foreach (var id in appointmentPatients) patientIds.Add(id);

        var memoryPatients = await db.Memories.AsNoTracking()
            .Where(x => x.CreatedAt >= since)
            .OrderByDescending(x => x.CreatedAt)
            .Select(x => x.UserId)
            .Take(opts.MaxPatientsPerRun * 2)
            .ToListAsync(ct)
            .ConfigureAwait(false);
        foreach (var id in memoryPatients) patientIds.Add(id);

        var userId = opts.UserId ?? Guid.Empty;
        var count = 0;
        foreach (var patientId in patientIds.Where(x => !reviewed.Contains(x)).Take(Math.Max(opts.MaxPatientsPerRun, 1)))
        {
            ct.ThrowIfCancellationRequested();
            try
            {
                await suggestions.SuggestAsync(
                    patientId,
                    userId,
                    opts.Goal,
                    $"daily-autonomy-review:{runAtUtc:yyyyMMdd}:{patientId:N}",
                    ct).ConfigureAwait(false);
                count++;
            }
            catch (Exception ex)
            {
                log.LogWarning(ex, "Daily autonomy review failed for patient {PatientId}", patientId);
            }
        }

        log.LogInformation("Daily autonomy review completed. PatientsReviewed={Count}", count);
        return count;
    }
}

internal sealed class AutonomyDailyReviewWorker(
    IServiceScopeFactory scopeFactory,
    IOptionsMonitor<AutonomyDailyReviewOptions> options,
    ILogger<AutonomyDailyReviewWorker> log) : BackgroundService
{
    private readonly Dictionary<string, DateOnly> _lastRun = new();

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        while (!stoppingToken.IsCancellationRequested)
        {
            var opts = options.CurrentValue;
            if (!opts.Enabled)
            {
                log.LogInformation("Autonomy daily review is disabled.");
                return;
            }

            var now = DateTimeOffset.UtcNow;
            var today = DateOnly.FromDateTime(now.UtcDateTime);
            if (opts.TimeUtc == now.ToString("HH:mm") &&
                (!_lastRun.TryGetValue("daily", out var last) || last != today))
            {
                _lastRun["daily"] = today;
                _ = RunReviewAsync(now, stoppingToken);
            }

            try
            {
                var nextMinute = new DateTime(now.Year, now.Month, now.Day, now.Hour, now.Minute, 0, DateTimeKind.Utc).AddMinutes(1);
                await Task.Delay(nextMinute - now.UtcDateTime, stoppingToken).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested) { }
        }
    }

    private async Task RunReviewAsync(DateTimeOffset runAt, CancellationToken ct)
    {
        try
        {
            using var scope = scopeFactory.CreateScope();
            var service = scope.ServiceProvider.GetRequiredService<IAutonomyDailyReviewService>();
            await service.RunOnceAsync(runAt, force: false, ct).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested) { }
        catch (Exception ex)
        {
            log.LogError(ex, "Autonomy daily review failed.");
        }
    }
}
