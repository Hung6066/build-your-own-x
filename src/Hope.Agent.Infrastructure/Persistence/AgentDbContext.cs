using Hope.Agent.Domain.Audit;
using Hope.Agent.Domain.Autonomy;
using Hope.Agent.Domain.Appointments;
using Hope.Agent.Domain.Clinical;
using Hope.Agent.Domain.Conversations;
using Hope.Agent.Domain.Eventing;
using Hope.Agent.Domain.Insights;
using Hope.Agent.Domain.Learning;
using Hope.Agent.Domain.Memory;
using Hope.Agent.Domain.Observability;
using Hope.Agent.Domain.Personalization;
using Hope.Agent.Domain.Rag;
using Hope.Agent.Domain.Security;
using Hope.Agent.Domain.Tasks;
using Hope.Agent.Domain.Training;
using Hope.Agent.Domain.UserModeling;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.ChangeTracking;

namespace Hope.Agent.Infrastructure.Persistence;

public sealed class AgentDbContext(DbContextOptions<AgentDbContext> options) : DbContext(options)
{
    private static readonly ValueComparer<Dictionary<string, string>> MetadataComparer = new(
        (left, right) => DictionaryEquals(left, right),
        value => DictionaryHashCode(value),
        value => CloneDictionary(value));

    public DbSet<Conversation> Conversations => Set<Conversation>();
    public DbSet<ConversationMessage> Messages => Set<ConversationMessage>();
    public DbSet<MemoryRecord> Memories => Set<MemoryRecord>();
    public DbSet<AuditEvent> AuditEvents => Set<AuditEvent>();
    public DbSet<AgentDecision> AgentDecisions => Set<AgentDecision>();
    public DbSet<AutonomousAction> AutonomousActions => Set<AutonomousAction>();
    public DbSet<AutonomyGoal> AutonomyGoals => Set<AutonomyGoal>();
    public DbSet<AutonomyReflection> AutonomyReflections => Set<AutonomyReflection>();
    public DbSet<AutonomyLearningFact> AutonomyLearningFacts => Set<AutonomyLearningFact>();
    public DbSet<AutonomyEvalGateRun> AutonomyEvalGateRuns => Set<AutonomyEvalGateRun>();
    public DbSet<AutonomyDriftSignal> AutonomyDriftSignals => Set<AutonomyDriftSignal>();
    public DbSet<AutonomyCompensationRecord> AutonomyCompensationRecords => Set<AutonomyCompensationRecord>();
    public DbSet<AutonomyReviewRecord> AutonomyReviewRecords => Set<AutonomyReviewRecord>();
    public DbSet<AuditReportRecord> AuditReports => Set<AuditReportRecord>();
    public DbSet<AppointmentBooking> AppointmentBookings => Set<AppointmentBooking>();
    public DbSet<OptimizationCostHint> OptimizationCostHints => Set<OptimizationCostHint>();
    public DbSet<MedicalSummaryRecord> MedicalSummaries => Set<MedicalSummaryRecord>();
    public DbSet<ReminderRecord> ReminderRecords => Set<ReminderRecord>();
    public DbSet<Document> Documents => Set<Document>();
    public DbSet<DocumentChunk> DocumentChunks => Set<DocumentChunk>();
    public DbSet<AgenticRagRun> AgenticRagRuns => Set<AgenticRagRun>();
    public DbSet<AgenticRagStep> AgenticRagSteps => Set<AgenticRagStep>();
    public DbSet<AgenticRagRetrieval> AgenticRagRetrievals => Set<AgenticRagRetrieval>();
    public DbSet<AgenticRagContextAssessment> AgenticRagContextAssessments => Set<AgenticRagContextAssessment>();
    public DbSet<Feedback> Feedback => Set<Feedback>();
    public DbSet<LearnedSkill> LearnedSkills => Set<LearnedSkill>();
    public DbSet<EvalRun> EvalRuns => Set<EvalRun>();
    public DbSet<EvalCase> EvalCases => Set<EvalCase>();
    public DbSet<RoutingStat> RoutingStats => Set<RoutingStat>();
    public DbSet<ShadowComparison> ShadowComparisons => Set<ShadowComparison>();
    public DbSet<ChallengerConfig> ChallengerConfigs => Set<ChallengerConfig>();
    public DbSet<AdversarialPattern> AdversarialPatterns => Set<AdversarialPattern>();
    public DbSet<ToolApprovalRequest> ToolApprovalRequests => Set<ToolApprovalRequest>();
    public DbSet<UserTrait> UserTraits => Set<UserTrait>();
    public DbSet<SessionSummary> SessionSummaries => Set<SessionSummary>();
    public DbSet<UserPreference> UserPreferences => Set<UserPreference>();
    public DbSet<ConversationSummary> ConversationSummaries => Set<ConversationSummary>();
    public DbSet<KanbanTask> KanbanTasks => Set<KanbanTask>();
    public DbSet<PreferenceRecord> PreferenceRecords => Set<PreferenceRecord>();
    public DbSet<FinetuneJob> FinetuneJobs => Set<FinetuneJob>();
    public DbSet<OutboxEvent> OutboxEvents => Set<OutboxEvent>();
    public DbSet<AgentOpsHourlyMetric> AgentOpsHourlyMetrics => Set<AgentOpsHourlyMetric>();
    public DbSet<TenantCostDaily> TenantCostDaily => Set<TenantCostDaily>();
    public DbSet<WorkflowSuccessDaily> WorkflowSuccessDaily => Set<WorkflowSuccessDaily>();
    public DbSet<ScalePartitionPolicy> ScalePartitionPolicies => Set<ScalePartitionPolicy>();
    public DbSet<ContextProvenanceRecord> ContextProvenanceRecords => Set<ContextProvenanceRecord>();
    public DbSet<SecurityIncidentRecord> SecurityIncidents => Set<SecurityIncidentRecord>();
    public DbSet<BreakGlassAccessRecord> BreakGlassAccessRecords => Set<BreakGlassAccessRecord>();
    public DbSet<AdversarialSimulationRun> AdversarialSimulationRuns => Set<AdversarialSimulationRun>();
    public DbSet<ApiKeyRecord> ApiKeyRecords => Set<ApiKeyRecord>();

    protected override void OnModelCreating(ModelBuilder b)
    {
        b.Entity<Conversation>(e =>
        {
            e.ToTable("conversations");
            e.HasKey(x => x.Id);
            e.Property(x => x.Title).HasMaxLength(256);
            e.HasMany(x => x.Messages).WithOne().HasForeignKey(m => m.ConversationId).OnDelete(DeleteBehavior.Cascade);
            e.HasIndex(x => x.UserId);
        });
        b.Entity<ConversationMessage>(e =>
        {
            e.ToTable("conversation_messages");
            e.HasKey(x => x.Id);
            e.Property(x => x.Content).HasColumnType("text");
            e.HasIndex(x => new { x.ConversationId, x.CreatedAt });
        });
        b.Entity<MemoryRecord>(e =>
        {
            e.ToTable("agent_memories");
            e.HasKey(x => x.Id);
            e.Property(x => x.Content).HasColumnType("text");
            e.Property(x => x.Metadata).HasColumnType("jsonb")
                .HasConversion(
                    v => System.Text.Json.JsonSerializer.Serialize(v, (System.Text.Json.JsonSerializerOptions?)null),
                    v => System.Text.Json.JsonSerializer.Deserialize<Dictionary<string, string>>(v, (System.Text.Json.JsonSerializerOptions?)null) ?? new())
                .Metadata.SetValueComparer(MetadataComparer);
            e.HasIndex(x => new { x.UserId, x.Kind });
            e.HasIndex(x => new { x.TenantId, x.UserId, x.Kind });
        });
        b.Entity<AuditEvent>(e =>
        {
            e.ToTable("audit_logs");
            e.HasKey(x => x.Id);
            e.Property(x => x.TenantId);
            e.Property(x => x.Actor).HasMaxLength(256);
            e.Property(x => x.Action).HasMaxLength(64);
            e.Property(x => x.ResourceType).HasMaxLength(128);
            e.Property(x => x.ResourceId).HasMaxLength(512);
            e.Property(x => x.PatientId).HasMaxLength(64);
            e.Property(x => x.CorrelationId).HasMaxLength(128);
            e.Property(x => x.DeploymentVersion).HasMaxLength(128);
            e.Property(x => x.PromptVersion).HasMaxLength(128);
            e.Property(x => x.ModelVersion).HasMaxLength(128);
            e.Property(x => x.ToolsetVersion).HasMaxLength(128);
            e.Property(x => x.PolicyVersion).HasMaxLength(128);
            e.Property(x => x.PayloadJson).HasColumnType("jsonb");
            e.HasIndex(x => new { x.OccurredAt, x.Action });
            e.HasIndex(x => x.CorrelationId);
            e.HasIndex(x => new { x.UserId, x.OccurredAt });
            e.HasIndex(x => new { x.TenantId, x.OccurredAt });
            e.HasIndex(x => new { x.TenantId, x.Action, x.OccurredAt });
            e.HasIndex(x => x.ResourceType);
        });
        b.Entity<AgentDecision>(e =>
        {
            e.ToTable("agent_decisions");
            e.HasKey(x => x.Id);
            e.Property(x => x.TenantId);
            e.Property(x => x.DecisionId).HasMaxLength(64);
            e.Property(x => x.Intent).HasMaxLength(64);
            e.Property(x => x.AgentProfile).HasMaxLength(64);
            e.Property(x => x.InputSummary).HasColumnType("text");
            e.Property(x => x.MemoryRefsJson).HasColumnType("jsonb");
            e.Property(x => x.EvidenceJson).HasColumnType("jsonb");
            e.Property(x => x.ProposedActionJson).HasColumnType("jsonb");
            e.Property(x => x.RiskLevel).HasConversion<int>();
            e.Property(x => x.PolicyDecision).HasConversion<int>();
            e.Property(x => x.DecisionStatus).HasConversion<int>();
            e.Property(x => x.Reason).HasMaxLength(512);
            e.Property(x => x.DeploymentVersion).HasMaxLength(128);
            e.Property(x => x.PromptVersion).HasMaxLength(128);
            e.Property(x => x.ModelVersion).HasMaxLength(128);
            e.Property(x => x.ToolsetVersion).HasMaxLength(128);
            e.Property(x => x.PolicyVersion).HasMaxLength(128);
            e.Property(x => x.CorrelationId).HasMaxLength(128);
            e.HasIndex(x => x.DecisionId).IsUnique();
            e.HasIndex(x => new { x.TenantId, x.CreatedAt });
            e.HasIndex(x => new { x.TenantId, x.PatientId, x.CreatedAt });
            e.HasIndex(x => new { x.PatientId, x.CreatedAt });
            e.HasIndex(x => new { x.UserId, x.CreatedAt });
            e.HasIndex(x => new { x.DecisionStatus, x.CreatedAt });
            e.HasIndex(x => new { x.TenantId, x.DecisionStatus, x.CreatedAt });
        });
        b.Entity<AutonomousAction>(e =>
        {
            e.ToTable("autonomous_actions");
            e.HasKey(x => x.Id);
            e.Property(x => x.TenantId);
            e.Property(x => x.ActionId).HasMaxLength(64);
            e.Property(x => x.DecisionId).HasMaxLength(64);
            e.Property(x => x.ToolName).HasMaxLength(128);
            e.Property(x => x.ArgumentsJson).HasColumnType("jsonb");
            e.Property(x => x.RiskLevel).HasConversion<int>();
            e.Property(x => x.Status).HasConversion<int>();
            e.Property(x => x.ResultJson).HasColumnType("jsonb");
            e.Property(x => x.Error).HasColumnType("text");
            e.Property(x => x.IdempotencyKey).HasMaxLength(128);
            e.Property(x => x.QueueBackend).HasMaxLength(64);
            e.Property(x => x.CompensationToolName).HasMaxLength(128);
            e.Property(x => x.CompensationArgumentsJson).HasColumnType("jsonb");
            e.Property(x => x.DeploymentVersion).HasMaxLength(128);
            e.Property(x => x.PromptVersion).HasMaxLength(128);
            e.Property(x => x.ModelVersion).HasMaxLength(128);
            e.Property(x => x.ToolsetVersion).HasMaxLength(128);
            e.Property(x => x.PolicyVersion).HasMaxLength(128);
            e.Property(x => x.CorrelationId).HasMaxLength(128);
            e.HasIndex(x => x.ActionId).IsUnique();
            e.HasIndex(x => x.DecisionId);
            e.HasIndex(x => x.IdempotencyKey);
            e.HasIndex(x => new { x.TenantId, x.Status, x.ScheduledFor });
            e.HasIndex(x => new { x.Status, x.ScheduledFor });
            e.HasIndex(x => new { x.ToolName, x.CreatedAt });
        });
        b.Entity<AutonomyGoal>(e =>
        {
            e.ToTable("autonomy_goals");
            e.HasKey(x => x.Id);
            e.Property(x => x.TenantId);
            e.Property(x => x.GoalId).HasMaxLength(64);
            e.Property(x => x.GoalType).HasMaxLength(64);
            e.Property(x => x.Description).HasColumnType("text");
            e.Property(x => x.EvidenceJson).HasColumnType("jsonb");
            e.Property(x => x.MaxAllowedRisk).HasConversion<int>();
            e.Property(x => x.Status).HasConversion<int>();
            e.Property(x => x.DecisionId).HasMaxLength(64);
            e.Property(x => x.Reason).HasMaxLength(512);
            e.Property(x => x.CorrelationId).HasMaxLength(128);
            e.HasIndex(x => x.GoalId).IsUnique();
            e.HasIndex(x => new { x.TenantId, x.CreatedAt });
            e.HasIndex(x => new { x.PatientId, x.CreatedAt });
            e.HasIndex(x => new { x.Status, x.CreatedAt });
            e.HasIndex(x => x.DecisionId);
        });
        b.Entity<AutonomyReflection>(e =>
        {
            e.ToTable("autonomy_reflections");
            e.HasKey(x => x.Id);
            e.Property(x => x.TenantId);
            e.Property(x => x.ReflectionId).HasMaxLength(64);
            e.Property(x => x.GoalId).HasMaxLength(64);
            e.Property(x => x.DecisionId).HasMaxLength(64);
            e.Property(x => x.ActionId).HasMaxLength(64);
            e.Property(x => x.Summary).HasColumnType("text");
            e.Property(x => x.LessonsJson).HasColumnType("jsonb");
            e.Property(x => x.CorrelationId).HasMaxLength(128);
            e.HasIndex(x => x.ReflectionId).IsUnique();
            e.HasIndex(x => new { x.TenantId, x.CreatedAt });
            e.HasIndex(x => new { x.PatientId, x.CreatedAt });
            e.HasIndex(x => x.ActionId);
        });
        b.Entity<AutonomyLearningFact>(e =>
        {
            e.ToTable("autonomy_learning_facts");
            e.HasKey(x => x.Id);
            e.Property(x => x.TenantId);
            e.Property(x => x.FactId).HasMaxLength(64);
            e.Property(x => x.Kind).HasConversion<int>();
            e.Property(x => x.Key).HasMaxLength(256);
            e.Property(x => x.ValueJson).HasColumnType("jsonb");
            e.Property(x => x.Source).HasMaxLength(128);
            e.HasIndex(x => x.FactId).IsUnique();
            e.HasIndex(x => new { x.Kind, x.Key }).IsUnique();
            e.HasIndex(x => new { x.TenantId, x.Kind, x.Key });
            e.HasIndex(x => x.LastObservedAt);
        });
        b.Entity<AutonomyEvalGateRun>(e =>
        {
            e.ToTable("autonomy_eval_gate_runs");
            e.HasKey(x => x.Id);
            e.Property(x => x.TenantId);
            e.Property(x => x.GateId).HasMaxLength(64);
            e.Property(x => x.SuiteName).HasMaxLength(128);
            e.Property(x => x.DeploymentVersion).HasMaxLength(128);
            e.Property(x => x.PromptVersion).HasMaxLength(128);
            e.Property(x => x.ModelVersion).HasMaxLength(128);
            e.Property(x => x.ToolsetVersion).HasMaxLength(128);
            e.Property(x => x.PolicyVersion).HasMaxLength(128);
            e.Property(x => x.MetricsJson).HasColumnType("jsonb");
            e.Property(x => x.Reason).HasMaxLength(512);
            e.Property(x => x.CorrelationId).HasMaxLength(128);
            e.HasIndex(x => x.GateId).IsUnique();
            e.HasIndex(x => new { x.TenantId, x.CreatedAt });
            e.HasIndex(x => new { x.SuiteName, x.CreatedAt });
            e.HasIndex(x => new { x.Passed, x.CreatedAt });
        });
        b.Entity<AutonomyDriftSignal>(e =>
        {
            e.ToTable("autonomy_drift_signals");
            e.HasKey(x => x.Id);
            e.Property(x => x.SignalId).HasMaxLength(64);
            e.Property(x => x.SignalType).HasMaxLength(128);
            e.Property(x => x.Severity).HasConversion<int>();
            e.Property(x => x.BaselineJson).HasColumnType("jsonb");
            e.Property(x => x.CurrentJson).HasColumnType("jsonb");
            e.Property(x => x.Status).HasConversion<int>();
            e.Property(x => x.CorrelationId).HasMaxLength(128);
            e.HasIndex(x => x.SignalId).IsUnique();
            e.HasIndex(x => new { x.Severity, x.CreatedAt });
            e.HasIndex(x => new { x.SignalType, x.CreatedAt });
        });
        b.Entity<AutonomyCompensationRecord>(e =>
        {
            e.ToTable("autonomy_compensations");
            e.HasKey(x => x.Id);
            e.Property(x => x.CompensationId).HasMaxLength(64);
            e.Property(x => x.ActionId).HasMaxLength(64);
            e.Property(x => x.ToolName).HasMaxLength(128);
            e.Property(x => x.ArgumentsJson).HasColumnType("jsonb");
            e.Property(x => x.Status).HasConversion<int>();
            e.Property(x => x.ResultJson).HasColumnType("jsonb");
            e.Property(x => x.Error).HasColumnType("text");
            e.Property(x => x.CorrelationId).HasMaxLength(128);
            e.HasIndex(x => x.CompensationId).IsUnique();
            e.HasIndex(x => x.ActionId);
            e.HasIndex(x => new { x.Status, x.CreatedAt });
        });
        b.Entity<AutonomyReviewRecord>(e =>
        {
            e.ToTable("autonomy_reviews");
            e.HasKey(x => x.Id);
            e.Property(x => x.ReviewId).HasMaxLength(64);
            e.Property(x => x.DecisionId).HasMaxLength(64);
            e.Property(x => x.ReviewerProfile).HasMaxLength(64);
            e.Property(x => x.Verdict).HasConversion<int>();
            e.Property(x => x.Notes).HasColumnType("text");
            e.Property(x => x.CorrelationId).HasMaxLength(128);
            e.HasIndex(x => x.ReviewId).IsUnique();
            e.HasIndex(x => x.DecisionId);
            e.HasIndex(x => new { x.Verdict, x.CreatedAt });
        });
        b.Entity<AppointmentBooking>(e =>
        {
            e.ToTable("appointment_bookings");
            e.HasKey(x => x.Id);
            e.Property(x => x.TenantId);
            e.Property(x => x.BookingId).HasMaxLength(64);
            e.Property(x => x.DoctorId).HasMaxLength(64);
            e.Property(x => x.SlotId).HasMaxLength(64);
            e.Property(x => x.Reason).HasColumnType("text");
            e.Property(x => x.Status).HasMaxLength(32);
            e.Property(x => x.CorrelationId).HasMaxLength(128);
            e.HasIndex(x => x.BookingId).IsUnique();
            e.HasIndex(x => new { x.TenantId, x.PatientId, x.ConfirmedAt });
            e.HasIndex(x => new { x.PatientId, x.ConfirmedAt });
            e.HasIndex(x => new { x.UserId, x.ConfirmedAt });
        });
        b.Entity<OptimizationCostHint>(e =>
        {
            e.ToTable("optimization_cost_hints");
            e.HasKey(x => x.Id);
            e.Property(x => x.DoctorId).HasMaxLength(64);
            e.Property(x => x.Specialty).HasMaxLength(128);
            e.HasIndex(x => new { x.DoctorId, x.Specialty }).IsUnique();
        });
        b.Entity<MedicalSummaryRecord>(e =>
        {
            e.ToTable("medical_summaries");
            e.HasKey(x => x.Id);
            e.Property(x => x.TenantId);
            e.Property(x => x.SummaryId).HasMaxLength(64);
            e.Property(x => x.SummaryType).HasMaxLength(64);
            e.Property(x => x.Audience).HasMaxLength(64);
            e.Property(x => x.Specialty).HasMaxLength(128);
            e.Property(x => x.SourceContext).HasColumnType("text");
            e.Property(x => x.SummaryText).HasColumnType("text");
            e.Property(x => x.Model).HasMaxLength(128);
            e.Property(x => x.Status).HasMaxLength(32);
            e.Property(x => x.CorrelationId).HasMaxLength(128);
            e.HasIndex(x => x.SummaryId).IsUnique();
            e.HasIndex(x => new { x.TenantId, x.PatientId, x.CreatedAt });
            e.HasIndex(x => new { x.PatientId, x.CreatedAt });
            e.HasIndex(x => new { x.UserId, x.CreatedAt });
        });
        b.Entity<ReminderRecord>(e =>
        {
            e.ToTable("reminder_records");
            e.HasKey(x => x.Id);
            e.Property(x => x.TenantId);
            e.Property(x => x.ReminderId).HasMaxLength(64);
            e.Property(x => x.WorkflowId).HasMaxLength(128);
            e.Property(x => x.ReminderType).HasMaxLength(64);
            e.Property(x => x.MedicationName).HasMaxLength(256);
            e.Property(x => x.Dosage).HasMaxLength(128);
            e.Property(x => x.Frequency).HasMaxLength(64);
            e.Property(x => x.PreferredChannel).HasMaxLength(64);
            e.Property(x => x.Status).HasMaxLength(32);
            e.Property(x => x.EscalationReason).HasColumnType("text");
            e.Property(x => x.CorrelationId).HasMaxLength(128);
            e.HasIndex(x => x.ReminderId).IsUnique();
            e.HasIndex(x => x.WorkflowId);
            e.HasIndex(x => new { x.TenantId, x.PatientId, x.StartAt });
            e.HasIndex(x => new { x.PatientId, x.StartAt });
            e.HasIndex(x => new { x.UserId, x.UpdatedAt });
        });
        b.Entity<AuditReportRecord>(e =>
        {
            e.ToTable("audit_reports");
            e.HasKey(x => x.Id);
            e.Property(x => x.ReportId).HasMaxLength(64);
            e.Property(x => x.ReportType).HasMaxLength(64);
            e.Property(x => x.Narrative).HasColumnType("text");
            e.Property(x => x.MetricsJson).HasColumnType("jsonb");
            e.Property(x => x.AnomaliesJson).HasColumnType("jsonb");
            e.Property(x => x.Format).HasMaxLength(16);
            e.Property(x => x.ExportPath).HasMaxLength(512);
            e.Property(x => x.IntegrityHash).HasMaxLength(128);
            e.Property(x => x.SigningAlgorithm).HasMaxLength(32);
            e.Property(x => x.Status).HasMaxLength(32);
            e.Property(x => x.CorrelationId).HasMaxLength(128);
            e.HasIndex(x => x.ReportId).IsUnique();
            e.HasIndex(x => new { x.RequestedBy, x.ExportedAt });
            e.HasIndex(x => new { x.ReportType, x.PeriodEnd });
        });
        b.Entity<Document>(e =>
        {
            e.ToTable("documents");
            e.HasKey(x => x.Id);
            e.Property(x => x.Title).HasMaxLength(512);
            e.Property(x => x.ContentHash).HasMaxLength(128);
            e.Property(x => x.Source).HasMaxLength(64);
            e.Property(x => x.Collection).HasMaxLength(128);
            e.Property(x => x.Metadata).HasColumnType("jsonb")
                .HasConversion(
                    v => System.Text.Json.JsonSerializer.Serialize(v, (System.Text.Json.JsonSerializerOptions?)null),
                    v => System.Text.Json.JsonSerializer.Deserialize<Dictionary<string, string>>(v, (System.Text.Json.JsonSerializerOptions?)null) ?? new())
                .Metadata.SetValueComparer(MetadataComparer);
            e.HasIndex(x => new { x.Collection, x.ContentHash }).IsUnique();
            e.HasIndex(x => new { x.Collection, x.Status });
        });
        b.Entity<DocumentChunk>(e =>
        {
            e.ToTable("document_chunks");
            e.HasKey(x => x.Id);
            e.Property(x => x.Content).HasColumnType("text");
            e.HasIndex(x => new { x.DocumentId, x.Ordinal });
            e.HasOne<Document>().WithMany().HasForeignKey(x => x.DocumentId).OnDelete(DeleteBehavior.Cascade);
        });
        b.Entity<AgenticRagRun>(e =>
        {
            e.ToTable("agentic_rag_runs");
            e.HasKey(x => x.Id);
            e.Property(x => x.RunId).HasMaxLength(64);
            e.Property(x => x.Query).HasColumnType("text");
            e.Property(x => x.Answer).HasColumnType("text");
            e.Property(x => x.Status).HasConversion<int>();
            e.Property(x => x.SelectedCorporaJson).HasColumnType("jsonb");
            e.Property(x => x.CitationsJson).HasColumnType("jsonb");
            e.Property(x => x.MetricsJson).HasColumnType("jsonb");
            e.Property(x => x.CorrelationId).HasMaxLength(128);
            e.HasIndex(x => x.RunId).IsUnique();
            e.HasIndex(x => new { x.TenantId, x.CreatedAt });
            e.HasIndex(x => new { x.PatientId, x.CreatedAt });
            e.HasIndex(x => new { x.Status, x.CreatedAt });
        });
        b.Entity<AgenticRagStep>(e =>
        {
            e.ToTable("agentic_rag_steps");
            e.HasKey(x => x.Id);
            e.Property(x => x.StepId).HasMaxLength(64);
            e.Property(x => x.RunId).HasMaxLength(64);
            e.Property(x => x.Kind).HasConversion<int>();
            e.Property(x => x.InputJson).HasColumnType("jsonb");
            e.Property(x => x.OutputJson).HasColumnType("jsonb");
            e.Property(x => x.CorrelationId).HasMaxLength(128);
            e.HasIndex(x => x.StepId).IsUnique();
            e.HasIndex(x => new { x.RunId, x.Iteration, x.CreatedAt });
        });
        b.Entity<AgenticRagRetrieval>(e =>
        {
            e.ToTable("agentic_rag_retrievals");
            e.HasKey(x => x.Id);
            e.Property(x => x.RetrievalId).HasMaxLength(64);
            e.Property(x => x.RunId).HasMaxLength(64);
            e.Property(x => x.Corpus).HasMaxLength(128);
            e.Property(x => x.Query).HasColumnType("text");
            e.Property(x => x.Source).HasMaxLength(128);
            e.Property(x => x.ReferenceId).HasMaxLength(128);
            e.Property(x => x.Title).HasMaxLength(512);
            e.Property(x => x.Content).HasColumnType("text");
            e.Property(x => x.Url).HasMaxLength(1024);
            e.Property(x => x.MetadataJson).HasColumnType("jsonb");
            e.HasIndex(x => x.RetrievalId).IsUnique();
            e.HasIndex(x => new { x.RunId, x.Corpus });
            e.HasIndex(x => new { x.RunId, x.Iteration, x.CreatedAt });
            e.HasIndex(x => new { x.Source, x.ReferenceId });
        });
        b.Entity<AgenticRagContextAssessment>(e =>
        {
            e.ToTable("agentic_rag_context_assessments");
            e.HasKey(x => x.Id);
            e.Property(x => x.AssessmentId).HasMaxLength(64);
            e.Property(x => x.RunId).HasMaxLength(64);
            e.Property(x => x.CoveredTermsJson).HasColumnType("jsonb");
            e.Property(x => x.MissingTermsJson).HasColumnType("jsonb");
            e.Property(x => x.Feedback).HasColumnType("text");
            e.HasIndex(x => x.AssessmentId).IsUnique();
            e.HasIndex(x => new { x.RunId, x.Iteration });
            e.HasIndex(x => new { x.Sufficient, x.CreatedAt });
        });

        b.Entity<Feedback>(e =>
        {
            e.ToTable("feedback");
            e.HasKey(x => x.Id);
            e.Property(x => x.Comment).HasColumnType("text");
            e.Property(x => x.Provider).HasMaxLength(64);
            e.Property(x => x.Model).HasMaxLength(128);
            e.Property(x => x.Intent).HasMaxLength(64);
            e.HasIndex(x => x.ConversationId);
            e.HasIndex(x => new { x.UserId, x.CreatedAt });
        });

        b.Entity<LearnedSkill>(e =>
        {
            e.ToTable("learned_skills");
            e.HasKey(x => x.Id);
            e.Property(x => x.Intent).HasMaxLength(64);
            e.Property(x => x.Signature).HasMaxLength(256);
            e.Property(x => x.ToolSequenceJson).HasColumnType("jsonb");
            e.Property(x => x.AnswerTemplate).HasColumnType("text");
            e.HasIndex(x => new { x.Intent, x.Reward });
            e.HasIndex(x => x.Signature);
        });

        b.Entity<EvalRun>(e =>
        {
            e.ToTable("eval_runs");
            e.HasKey(x => x.Id);
            e.Property(x => x.TenantId);
            e.Property(x => x.Suite).HasMaxLength(64);
            e.Property(x => x.DeploymentVersion).HasMaxLength(128);
            e.Property(x => x.PromptVersion).HasMaxLength(128);
            e.Property(x => x.ModelVersion).HasMaxLength(128);
            e.Property(x => x.ToolsetVersion).HasMaxLength(128);
            e.Property(x => x.PolicyVersion).HasMaxLength(128);
            e.Property(x => x.ReportJson).HasColumnType("jsonb");
            e.HasIndex(x => new { x.Suite, x.StartedAt });
            e.HasIndex(x => new { x.TenantId, x.Suite, x.StartedAt });
        });

        b.Entity<EvalCase>(e =>
        {
            e.ToTable("eval_cases");
            e.HasKey(x => x.Id);
            e.Property(x => x.Suite).HasMaxLength(64);
            e.Property(x => x.Name).HasMaxLength(256);
            e.Property(x => x.UserMessage).HasColumnType("text");
            e.Property(x => x.ReferenceAnswer).HasColumnType("text");
            e.Property(x => x.Tags).HasMaxLength(512);
            e.HasIndex(x => new { x.Suite, x.Active });
        });

        b.Entity<RoutingStat>(e =>
        {
            e.ToTable("routing_stats");
            e.HasKey(x => x.Id);
            e.Property(x => x.Intent).HasMaxLength(64);
            e.Property(x => x.Provider).HasMaxLength(64);
            e.Property(x => x.Model).HasMaxLength(128);
            e.HasIndex(x => new { x.Intent, x.Provider, x.Model }).IsUnique();
        });

        b.Entity<ShadowComparison>(e =>
        {
            e.ToTable("shadow_comparisons");
            e.HasKey(x => x.Id);
            e.Property(x => x.Intent).HasMaxLength(64);
            e.Property(x => x.ChampionProvider).HasMaxLength(64);
            e.Property(x => x.ChallengerProvider).HasMaxLength(64);
            e.HasIndex(x => new { x.Intent, x.CreatedAt });
        });

        b.Entity<ChallengerConfig>(e =>
        {
            e.ToTable("challenger_configs");
            e.HasKey(x => x.Id);
            e.Property(x => x.Intent).HasMaxLength(64);
            e.Property(x => x.ChallengerProvider).HasMaxLength(64);
            e.HasIndex(x => new { x.Intent, x.Active });
        });

        b.Entity<AdversarialPattern>(e =>
        {
            e.ToTable("adversarial_patterns");
            e.HasKey(x => x.Id);
            e.Property(x => x.Signature).HasMaxLength(64);
            e.Property(x => x.Sample).HasColumnType("text");
            e.Property(x => x.Reason).HasMaxLength(128);
            e.HasIndex(x => x.Signature).IsUnique();
            e.HasIndex(x => x.Active);
        });

        b.Entity<ApiKeyRecord>(e =>
        {
            e.ToTable("api_key_records");
            e.HasKey(x => x.Id);
            e.Property(x => x.TenantId).IsRequired();
            e.Property(x => x.Name).HasMaxLength(128);
            e.Property(x => x.Hash).HasMaxLength(128);
            e.Property(x => x.Scope).HasMaxLength(256);
            e.Property(x => x.CreatedBy).HasMaxLength(128);
            e.Property(x => x.RevokedBy).HasMaxLength(128);
            e.Property(x => x.Reason).HasColumnType("text");
            e.HasIndex(x => x.Hash).IsUnique();
            e.HasIndex(x => new { x.TenantId, x.Revoked, x.ExpiresAt });
        });

        b.Entity<ContextProvenanceRecord>(e =>
        {
            e.ToTable("context_provenance_records");
            e.HasKey(x => x.Id);
            e.Property(x => x.DecisionId).HasMaxLength(64);
            e.Property(x => x.ActionId).HasMaxLength(64);
            e.Property(x => x.CorrelationId).HasMaxLength(128);
            e.Property(x => x.AnswerHash).HasMaxLength(96);
            e.Property(x => x.RetrievalQuery).HasColumnType("text");
            e.Property(x => x.SourceManifestJson).HasColumnType("jsonb");
            e.Property(x => x.DroppedContextJson).HasColumnType("jsonb");
            e.Property(x => x.Purpose).HasMaxLength(64);
            e.Property(x => x.Sensitivity).HasMaxLength(32);
            e.Property(x => x.PolicyVersion).HasMaxLength(128);
            e.HasIndex(x => new { x.TenantId, x.PatientId, x.CreatedAt });
            e.HasIndex(x => x.DecisionId);
            e.HasIndex(x => x.CorrelationId);
        });

        b.Entity<SecurityIncidentRecord>(e =>
        {
            e.ToTable("security_incidents");
            e.HasKey(x => x.Id);
            e.Property(x => x.IncidentType).HasMaxLength(64);
            e.Property(x => x.Severity).HasMaxLength(32);
            e.Property(x => x.Status).HasMaxLength(32);
            e.Property(x => x.Summary).HasColumnType("text");
            e.Property(x => x.AgentProfile).HasMaxLength(128);
            e.Property(x => x.ToolName).HasMaxLength(128);
            e.Property(x => x.RunbookJson).HasColumnType("jsonb");
            e.Property(x => x.ForensicExportJson).HasColumnType("jsonb");
            e.Property(x => x.CorrelationId).HasMaxLength(128);
            e.HasIndex(x => new { x.TenantId, x.Status, x.CreatedAt });
            e.HasIndex(x => x.CorrelationId);
        });

        b.Entity<BreakGlassAccessRecord>(e =>
        {
            e.ToTable("break_glass_access_records");
            e.HasKey(x => x.Id);
            e.Property(x => x.Purpose).HasMaxLength(64);
            e.Property(x => x.Reason).HasColumnType("text");
            e.Property(x => x.Status).HasMaxLength(64);
            e.Property(x => x.Reviewer).HasMaxLength(128);
            e.Property(x => x.CorrelationId).HasMaxLength(128);
            e.HasIndex(x => new { x.TenantId, x.Status, x.ReviewDueAt });
            e.HasIndex(x => x.ActorUserId);
        });

        b.Entity<AdversarialSimulationRun>(e =>
        {
            e.ToTable("adversarial_simulation_runs");
            e.HasKey(x => x.Id);
            e.Property(x => x.SimulationId).HasMaxLength(64);
            e.Property(x => x.TargetEnvironment).HasMaxLength(64);
            e.Property(x => x.SuitesJson).HasColumnType("jsonb");
            e.Property(x => x.FindingsJson).HasColumnType("jsonb");
            e.Property(x => x.PolicyVersion).HasMaxLength(128);
            e.Property(x => x.CorrelationId).HasMaxLength(128);
            e.HasIndex(x => x.SimulationId).IsUnique();
            e.HasIndex(x => new { x.TenantId, x.CreatedAt });
            e.HasIndex(x => new { x.Passed, x.CreatedAt });
        });

        b.Entity<ToolApprovalRequest>(e =>
        {
            e.ToTable("tool_approval_requests");
            e.HasKey(x => x.Id);
            e.Property(x => x.ToolName).HasMaxLength(128);
            e.Property(x => x.ArgumentsJson).HasColumnType("jsonb");
            e.Property(x => x.Reason).HasMaxLength(512);
            e.Property(x => x.Impact).HasConversion<int>();
            e.Property(x => x.Status).HasConversion<int>();
            e.HasIndex(x => new { x.Status, x.RequestedAt });
            e.HasIndex(x => x.ConversationId);
        });

        b.Entity<UserTrait>(e =>
        {
            e.ToTable("user_traits");
            e.HasKey(x => x.UserId);
            e.Property(x => x.Role).HasMaxLength(64);
            e.Property(x => x.Specialty).HasMaxLength(128);
            e.Property(x => x.CommunicationStyle).HasMaxLength(64);
            e.Property(x => x.PreferredLanguage).HasMaxLength(16);
        });

        b.Entity<SessionSummary>(e =>
        {
            e.ToTable("session_summaries");
            e.HasKey(x => x.Id);
            e.Property(x => x.Content).HasColumnType("text");
            e.HasIndex(x => new { x.UserId, x.PeriodEnd });
        });

        b.Entity<UserPreference>(e =>
        {
            e.ToTable("user_preferences");
            e.HasKey(x => x.UserId);
            e.Property(x => x.TenantId).IsRequired();
            e.Property(x => x.AgentProfile).HasMaxLength(64);
            e.Property(x => x.PreferredProvider).HasMaxLength(64);
            e.Property(x => x.PreferredModel).HasMaxLength(128);
            e.Property(x => x.PreferredLanguage).HasMaxLength(16);
            e.Property(x => x.PreferredChannel).HasMaxLength(32);
            e.Property(x => x.Persona).HasMaxLength(64);
            e.Property(x => x.SafetyMode).HasMaxLength(64);
            e.Property(x => x.Purpose).HasMaxLength(64);
            e.Property(x => x.PreferencesJson).HasColumnType("jsonb");
            e.Property(x => x.Version).HasMaxLength(128);
            e.Property(x => x.UpdatedBy).HasMaxLength(128);
            e.HasIndex(x => new { x.TenantId, x.UpdatedAt });
        });

        b.Entity<ConversationSummary>(e =>
        {
            e.ToTable("conversation_summaries");
            e.HasKey(x => x.ConversationId);
            e.Property(x => x.Content).HasColumnType("text");
        });

        b.Entity<KanbanTask>(e =>
        {
            e.ToTable("kanban_tasks");
            e.HasKey(x => x.Id);
            e.Property(x => x.Title).HasMaxLength(256);
            e.Property(x => x.Description).HasColumnType("text");
            e.Property(x => x.PatientRef).HasMaxLength(64);
            e.Property(x => x.AssignedTo).HasMaxLength(128);
            e.Property(x => x.Tags).HasMaxLength(256);
            e.Property(x => x.Column).HasConversion<int>();
            e.Property(x => x.Priority).HasConversion<int>();
            e.HasIndex(x => new { x.Column, x.UpdatedAt });
            e.HasIndex(x => x.UserId);
            e.HasIndex(x => x.PatientRef);
        });

        b.Entity<PreferenceRecord>(e =>
        {
            e.ToTable("preference_records");
            e.HasKey(x => x.Id);
            e.Property(x => x.Prompt).HasColumnType("text");
            e.Property(x => x.ChosenResponse).HasColumnType("text");
            e.Property(x => x.RejectedResponse).HasColumnType("text");
            e.Property(x => x.Rationale).HasColumnType("text");
            e.Property(x => x.ChosenProvider).HasMaxLength(64);
            e.Property(x => x.RejectedProvider).HasMaxLength(64);
            e.Property(x => x.Specialty).HasMaxLength(128);
            e.HasIndex(x => new { x.CreatedAt, x.Specialty });
            e.HasIndex(x => x.ConversationId);
        });

        b.Entity<FinetuneJob>(e =>
        {
            e.ToTable("finetune_jobs");
            e.HasKey(x => x.Id);
            e.Property(x => x.BaseModel).HasMaxLength(256);
            e.Property(x => x.OutputModelTag).HasMaxLength(256);
            e.Property(x => x.RemoteJobId).HasMaxLength(256);
            e.Property(x => x.ProgressJson).HasColumnType("jsonb");
            e.Property(x => x.ErrorDetail).HasColumnType("text");
            e.Property(x => x.Status).HasConversion<int>();
            e.Property(x => x.JobType).HasConversion<int>();
            e.HasIndex(x => new { x.Status, x.CreatedAt });
        });

        b.Entity<OutboxEvent>(e =>
        {
            e.ToTable("outbox_events");
            e.HasKey(x => x.Id);
            e.Property(x => x.Topic).HasMaxLength(256);
            e.Property(x => x.Key).HasMaxLength(256);
            e.Property(x => x.PayloadJson).HasColumnType("jsonb");
            e.Property(x => x.HeadersJson).HasColumnType("jsonb");
            e.Property(x => x.Status).HasConversion<int>();
            e.Property(x => x.LastError).HasColumnType("text");
            e.Property(x => x.CorrelationId).HasMaxLength(128);
            e.Property(x => x.IdempotencyKey).HasMaxLength(256);
            e.HasIndex(x => new { x.Status, x.ScheduledFor });
            e.HasIndex(x => new { x.TenantId, x.Status, x.ScheduledFor });
            e.HasIndex(x => new { x.Topic, x.CreatedAt });
            e.HasIndex(x => x.IdempotencyKey);
        });

        b.Entity<AgentOpsHourlyMetric>(e =>
        {
            e.ToTable("agent_ops_hourly_metrics");
            e.HasKey(x => x.Id);
            e.Property(x => x.AgentProfile).HasMaxLength(128);
            e.Property(x => x.CostUsd).HasPrecision(18, 6);
            e.HasIndex(x => new { x.TenantId, x.HourBucket, x.AgentProfile }).IsUnique();
            e.HasIndex(x => x.HourBucket);
        });

        b.Entity<TenantCostDaily>(e =>
        {
            e.ToTable("tenant_cost_daily");
            e.HasKey(x => x.Id);
            e.Property(x => x.AgentProfile).HasMaxLength(128);
            e.Property(x => x.Model).HasMaxLength(128);
            e.Property(x => x.CostUsd).HasPrecision(18, 6);
            e.HasIndex(x => new { x.TenantId, x.DayBucket, x.AgentProfile, x.Model }).IsUnique();
            e.HasIndex(x => x.DayBucket);
        });

        b.Entity<WorkflowSuccessDaily>(e =>
        {
            e.ToTable("workflow_success_daily");
            e.HasKey(x => x.Id);
            e.Property(x => x.WorkflowName).HasMaxLength(128);
            e.HasIndex(x => new { x.TenantId, x.DayBucket, x.WorkflowName }).IsUnique();
            e.HasIndex(x => x.DayBucket);
        });

        b.Entity<ScalePartitionPolicy>(e =>
        {
            e.ToTable("scale_partition_policies");
            e.HasKey(x => x.Id);
            e.Property(x => x.TableName).HasMaxLength(128);
            e.Property(x => x.PartitionKey).HasMaxLength(128);
            e.Property(x => x.Strategy).HasMaxLength(64);
            e.HasIndex(x => x.TableName).IsUnique();
        });
    }
    private static bool DictionaryEquals(Dictionary<string, string>? left, Dictionary<string, string>? right)
    {
        if (ReferenceEquals(left, right)) return true;
        if (left is null || right is null || left.Count != right.Count) return false;

        foreach (var kvp in left)
        {
            if (!right.TryGetValue(kvp.Key, out var value) || !string.Equals(value, kvp.Value, StringComparison.Ordinal))
                return false;
        }

        return true;
    }

    private static int DictionaryHashCode(Dictionary<string, string>? value)
    {
        if (value is null || value.Count == 0) return 0;

        var hash = 17;
        foreach (var kvp in value.OrderBy(x => x.Key, StringComparer.Ordinal))
        {
            hash = HashCode.Combine(hash, StringComparer.Ordinal.GetHashCode(kvp.Key));
            hash = HashCode.Combine(hash, kvp.Value is null ? 0 : StringComparer.Ordinal.GetHashCode(kvp.Value));
        }

        return hash;
    }

    private static Dictionary<string, string> CloneDictionary(Dictionary<string, string>? value)
        => value is null ? new Dictionary<string, string>() : value.ToDictionary(kvp => kvp.Key, kvp => kvp.Value, StringComparer.Ordinal);
}
