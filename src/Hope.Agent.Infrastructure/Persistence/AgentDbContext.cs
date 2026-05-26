using Hope.Agent.Domain.Audit;
using Hope.Agent.Domain.Conversations;
using Hope.Agent.Domain.Insights;
using Hope.Agent.Domain.Learning;
using Hope.Agent.Domain.Memory;
using Hope.Agent.Domain.Personalization;
using Hope.Agent.Domain.Rag;
using Hope.Agent.Domain.Security;
using Hope.Agent.Domain.Tasks;
using Hope.Agent.Domain.Training;
using Hope.Agent.Domain.UserModeling;
using Microsoft.EntityFrameworkCore;

namespace Hope.Agent.Infrastructure.Persistence;

public sealed class AgentDbContext(DbContextOptions<AgentDbContext> options) : DbContext(options)
{
    public DbSet<Conversation> Conversations => Set<Conversation>();
    public DbSet<ConversationMessage> Messages => Set<ConversationMessage>();
    public DbSet<MemoryRecord> Memories => Set<MemoryRecord>();
    public DbSet<AuditEvent> AuditEvents => Set<AuditEvent>();
    public DbSet<Document> Documents => Set<Document>();
    public DbSet<DocumentChunk> DocumentChunks => Set<DocumentChunk>();
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
                    v => System.Text.Json.JsonSerializer.Deserialize<Dictionary<string, string>>(v, (System.Text.Json.JsonSerializerOptions?)null) ?? new());
            e.HasIndex(x => new { x.UserId, x.Kind });
        });
        b.Entity<AuditEvent>(e =>
        {
            e.ToTable("audit_logs");
            e.HasKey(x => x.Id);
            e.Property(x => x.PayloadJson).HasColumnType("jsonb");
            e.HasIndex(x => new { x.OccurredAt, x.Action });
            e.HasIndex(x => x.CorrelationId);
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
                    v => System.Text.Json.JsonSerializer.Deserialize<Dictionary<string, string>>(v, (System.Text.Json.JsonSerializerOptions?)null) ?? new());
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
            e.Property(x => x.Suite).HasMaxLength(64);
            e.Property(x => x.ReportJson).HasColumnType("jsonb");
            e.HasIndex(x => new { x.Suite, x.StartedAt });
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
            e.Property(x => x.AgentProfile).HasMaxLength(64);
            e.Property(x => x.PreferredProvider).HasMaxLength(64);
            e.Property(x => x.PreferredModel).HasMaxLength(128);
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
    }
}
