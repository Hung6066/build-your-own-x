using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Hope.Agent.Infrastructure.Migrations;

/// <inheritdoc />
public partial class InitialCreate : Microsoft.EntityFrameworkCore.Migrations.Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "adversarial_patterns",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    Signature = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: false),
                    Sample = table.Column<string>(type: "text", nullable: false),
                    Reason = table.Column<string>(type: "character varying(128)", maxLength: 128, nullable: false),
                    Hits = table.Column<int>(type: "integer", nullable: false),
                    Active = table.Column<bool>(type: "boolean", nullable: false),
                    Confidence = table.Column<double>(type: "double precision", nullable: false),
                    FirstSeen = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    LastSeen = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    PromotedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_adversarial_patterns", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "agent_memories",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    UserId = table.Column<Guid>(type: "uuid", nullable: false),
                    ConversationId = table.Column<Guid>(type: "uuid", nullable: true),
                    Kind = table.Column<int>(type: "integer", nullable: false),
                    Content = table.Column<string>(type: "text", nullable: false),
                    Source = table.Column<string>(type: "text", nullable: true),
                    Importance = table.Column<float>(type: "real", nullable: false),
                    Metadata = table.Column<string>(type: "jsonb", nullable: false),
                    CreatedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_agent_memories", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "audit_logs",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    OccurredAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    UserId = table.Column<Guid>(type: "uuid", nullable: true),
                    Actor = table.Column<string>(type: "character varying(256)", maxLength: 256, nullable: false),
                    Action = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: false),
                    ResourceType = table.Column<string>(type: "character varying(128)", maxLength: 128, nullable: true),
                    ResourceId = table.Column<string>(type: "character varying(512)", maxLength: 512, nullable: true),
                    PatientId = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: true),
                    CorrelationId = table.Column<string>(type: "character varying(128)", maxLength: 128, nullable: true),
                    Reason = table.Column<string>(type: "text", nullable: true),
                    PayloadJson = table.Column<string>(type: "jsonb", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_audit_logs", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "challenger_configs",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    Intent = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: false),
                    ChallengerProvider = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: false),
                    TrafficFraction = table.Column<double>(type: "double precision", nullable: false),
                    MinSamples = table.Column<int>(type: "integer", nullable: false),
                    PromotionWinRate = table.Column<double>(type: "double precision", nullable: false),
                    Active = table.Column<bool>(type: "boolean", nullable: false),
                    Promoted = table.Column<bool>(type: "boolean", nullable: false),
                    CreatedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    PromotedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_challenger_configs", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "conversation_summaries",
                columns: table => new
                {
                    ConversationId = table.Column<Guid>(type: "uuid", nullable: false),
                    Content = table.Column<string>(type: "text", nullable: false),
                    SummarizedMessageCount = table.Column<int>(type: "integer", nullable: false),
                    SummarizedUpTo = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    UpdatedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_conversation_summaries", x => x.ConversationId);
                });

            migrationBuilder.CreateTable(
                name: "conversations",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    UserId = table.Column<Guid>(type: "uuid", nullable: false),
                    Title = table.Column<string>(type: "character varying(256)", maxLength: 256, nullable: false),
                    CreatedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    UpdatedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_conversations", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "documents",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    Title = table.Column<string>(type: "character varying(512)", maxLength: 512, nullable: false),
                    Source = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: false),
                    Collection = table.Column<string>(type: "character varying(128)", maxLength: 128, nullable: false),
                    Url = table.Column<string>(type: "text", nullable: true),
                    ContentHash = table.Column<string>(type: "character varying(128)", maxLength: 128, nullable: false),
                    Status = table.Column<int>(type: "integer", nullable: false),
                    ChunkCount = table.Column<int>(type: "integer", nullable: false),
                    Metadata = table.Column<string>(type: "jsonb", nullable: false),
                    CreatedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    UpdatedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_documents", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "eval_cases",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    Suite = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: false),
                    Name = table.Column<string>(type: "character varying(256)", maxLength: 256, nullable: false),
                    UserMessage = table.Column<string>(type: "text", nullable: false),
                    ReferenceAnswer = table.Column<string>(type: "text", nullable: false),
                    Tags = table.Column<string>(type: "character varying(512)", maxLength: 512, nullable: true),
                    Active = table.Column<bool>(type: "boolean", nullable: false),
                    CreatedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_eval_cases", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "eval_runs",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    Suite = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: false),
                    Total = table.Column<int>(type: "integer", nullable: false),
                    Passed = table.Column<int>(type: "integer", nullable: false),
                    Failed = table.Column<int>(type: "integer", nullable: false),
                    AvgJudgeScore = table.Column<double>(type: "double precision", nullable: false),
                    EloRating = table.Column<double>(type: "double precision", nullable: false),
                    StartedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    FinishedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    ReportJson = table.Column<string>(type: "jsonb", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_eval_runs", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "feedback",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    UserId = table.Column<Guid>(type: "uuid", nullable: false),
                    ConversationId = table.Column<Guid>(type: "uuid", nullable: false),
                    Rating = table.Column<int>(type: "integer", nullable: false),
                    Comment = table.Column<string>(type: "text", nullable: true),
                    Provider = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: true),
                    Model = table.Column<string>(type: "character varying(128)", maxLength: 128, nullable: true),
                    Intent = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: true),
                    CreatedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_feedback", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "finetune_jobs",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    JobType = table.Column<int>(type: "integer", nullable: false),
                    Status = table.Column<int>(type: "integer", nullable: false),
                    BaseModel = table.Column<string>(type: "character varying(256)", maxLength: 256, nullable: false),
                    OutputModelTag = table.Column<string>(type: "character varying(256)", maxLength: 256, nullable: true),
                    DataSince = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    DataUntil = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    RecordCount = table.Column<int>(type: "integer", nullable: false),
                    RemoteJobId = table.Column<string>(type: "character varying(256)", maxLength: 256, nullable: true),
                    ProgressJson = table.Column<string>(type: "jsonb", nullable: true),
                    EloScore = table.Column<double>(type: "double precision", nullable: true),
                    ErrorDetail = table.Column<string>(type: "text", nullable: true),
                    CreatedByUserId = table.Column<Guid>(type: "uuid", nullable: false),
                    CreatedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    StartedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    FinishedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_finetune_jobs", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "kanban_tasks",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    UserId = table.Column<Guid>(type: "uuid", nullable: true),
                    ConversationId = table.Column<Guid>(type: "uuid", nullable: true),
                    PatientRef = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: true),
                    Title = table.Column<string>(type: "character varying(256)", maxLength: 256, nullable: false),
                    Description = table.Column<string>(type: "text", nullable: true),
                    Column = table.Column<int>(type: "integer", nullable: false),
                    Priority = table.Column<int>(type: "integer", nullable: false),
                    CreatedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    UpdatedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    DueAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    CompletedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    AssignedTo = table.Column<string>(type: "character varying(128)", maxLength: 128, nullable: true),
                    Tags = table.Column<string>(type: "character varying(256)", maxLength: 256, nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_kanban_tasks", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "learned_skills",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    Intent = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: false),
                    Signature = table.Column<string>(type: "character varying(256)", maxLength: 256, nullable: false),
                    ToolSequenceJson = table.Column<string>(type: "jsonb", nullable: false),
                    AnswerTemplate = table.Column<string>(type: "text", nullable: false),
                    Reward = table.Column<double>(type: "double precision", nullable: false),
                    UsageCount = table.Column<long>(type: "bigint", nullable: false),
                    CreatedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    LastUsed = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_learned_skills", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "preference_records",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    ConversationId = table.Column<Guid>(type: "uuid", nullable: false),
                    MessageId = table.Column<Guid>(type: "uuid", nullable: false),
                    Prompt = table.Column<string>(type: "text", nullable: false),
                    ChosenResponse = table.Column<string>(type: "text", nullable: false),
                    RejectedResponse = table.Column<string>(type: "text", nullable: false),
                    ChosenProvider = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: true),
                    RejectedProvider = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: true),
                    Rationale = table.Column<string>(type: "text", nullable: true),
                    Specialty = table.Column<string>(type: "character varying(128)", maxLength: 128, nullable: true),
                    RatedByUserId = table.Column<Guid>(type: "uuid", nullable: false),
                    CreatedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_preference_records", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "routing_stats",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    Intent = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: false),
                    Provider = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: false),
                    Model = table.Column<string>(type: "character varying(128)", maxLength: 128, nullable: false),
                    Pulls = table.Column<long>(type: "bigint", nullable: false),
                    TotalReward = table.Column<double>(type: "double precision", nullable: false),
                    TotalLatencyMs = table.Column<double>(type: "double precision", nullable: false),
                    Failures = table.Column<long>(type: "bigint", nullable: false),
                    UpdatedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_routing_stats", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "session_summaries",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    UserId = table.Column<Guid>(type: "uuid", nullable: false),
                    PeriodStart = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    PeriodEnd = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    ConversationCount = table.Column<int>(type: "integer", nullable: false),
                    Content = table.Column<string>(type: "text", nullable: false),
                    CreatedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_session_summaries", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "shadow_comparisons",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    Intent = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: false),
                    ChampionProvider = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: false),
                    ChallengerProvider = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: false),
                    ChampionScore = table.Column<double>(type: "double precision", nullable: false),
                    ChallengerScore = table.Column<double>(type: "double precision", nullable: false),
                    ChallengerWon = table.Column<bool>(type: "boolean", nullable: false),
                    LatencyDeltaMs = table.Column<double>(type: "double precision", nullable: false),
                    CreatedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_shadow_comparisons", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "tool_approval_requests",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    ConversationId = table.Column<Guid>(type: "uuid", nullable: false),
                    UserId = table.Column<Guid>(type: "uuid", nullable: false),
                    ToolName = table.Column<string>(type: "character varying(128)", maxLength: 128, nullable: false),
                    ArgumentsJson = table.Column<string>(type: "jsonb", nullable: false),
                    Impact = table.Column<int>(type: "integer", nullable: false),
                    RequestedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    Status = table.Column<int>(type: "integer", nullable: false),
                    DecidedBy = table.Column<Guid>(type: "uuid", nullable: true),
                    DecidedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    Reason = table.Column<string>(type: "character varying(512)", maxLength: 512, nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_tool_approval_requests", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "user_preferences",
                columns: table => new
                {
                    UserId = table.Column<Guid>(type: "uuid", nullable: false),
                    AgentProfile = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: true),
                    PreferredProvider = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: true),
                    PreferredModel = table.Column<string>(type: "character varying(128)", maxLength: 128, nullable: true),
                    UpdatedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_user_preferences", x => x.UserId);
                });

            migrationBuilder.CreateTable(
                name: "user_traits",
                columns: table => new
                {
                    UserId = table.Column<Guid>(type: "uuid", nullable: false),
                    Role = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: true),
                    Specialty = table.Column<string>(type: "character varying(128)", maxLength: 128, nullable: true),
                    CommunicationStyle = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: true),
                    PreferredLanguage = table.Column<string>(type: "character varying(16)", maxLength: 16, nullable: true),
                    TurnsAtLastExtract = table.Column<int>(type: "integer", nullable: false),
                    UpdatedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_user_traits", x => x.UserId);
                });

            migrationBuilder.CreateTable(
                name: "conversation_messages",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    ConversationId = table.Column<Guid>(type: "uuid", nullable: false),
                    Role = table.Column<int>(type: "integer", nullable: false),
                    Content = table.Column<string>(type: "text", nullable: false),
                    ToolName = table.Column<string>(type: "text", nullable: true),
                    ToolCallId = table.Column<string>(type: "text", nullable: true),
                    PromptTokens = table.Column<int>(type: "integer", nullable: true),
                    CompletionTokens = table.Column<int>(type: "integer", nullable: true),
                    CreatedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_conversation_messages", x => x.Id);
                    table.ForeignKey(
                        name: "FK_conversation_messages_conversations_ConversationId",
                        column: x => x.ConversationId,
                        principalTable: "conversations",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "document_chunks",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    DocumentId = table.Column<Guid>(type: "uuid", nullable: false),
                    Ordinal = table.Column<int>(type: "integer", nullable: false),
                    Content = table.Column<string>(type: "text", nullable: false),
                    TokenEstimate = table.Column<int>(type: "integer", nullable: false),
                    SectionPath = table.Column<string>(type: "text", nullable: true),
                    CreatedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_document_chunks", x => x.Id);
                    table.ForeignKey(
                        name: "FK_document_chunks_documents_DocumentId",
                        column: x => x.DocumentId,
                        principalTable: "documents",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateIndex(
                name: "IX_adversarial_patterns_Active",
                table: "adversarial_patterns",
                column: "Active");

            migrationBuilder.CreateIndex(
                name: "IX_adversarial_patterns_Signature",
                table: "adversarial_patterns",
                column: "Signature",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_agent_memories_UserId_Kind",
                table: "agent_memories",
                columns: new[] { "UserId", "Kind" });

            migrationBuilder.CreateIndex(
                name: "IX_audit_logs_CorrelationId",
                table: "audit_logs",
                column: "CorrelationId");

            migrationBuilder.CreateIndex(
                name: "IX_audit_logs_OccurredAt_Action",
                table: "audit_logs",
                columns: new[] { "OccurredAt", "Action" });

            migrationBuilder.CreateIndex(
                name: "IX_audit_logs_ResourceType",
                table: "audit_logs",
                column: "ResourceType");

            migrationBuilder.CreateIndex(
                name: "IX_audit_logs_UserId_OccurredAt",
                table: "audit_logs",
                columns: new[] { "UserId", "OccurredAt" });

            migrationBuilder.CreateIndex(
                name: "IX_challenger_configs_Intent_Active",
                table: "challenger_configs",
                columns: new[] { "Intent", "Active" });

            migrationBuilder.CreateIndex(
                name: "IX_conversation_messages_ConversationId_CreatedAt",
                table: "conversation_messages",
                columns: new[] { "ConversationId", "CreatedAt" });

            migrationBuilder.CreateIndex(
                name: "IX_conversations_UserId",
                table: "conversations",
                column: "UserId");

            migrationBuilder.CreateIndex(
                name: "IX_document_chunks_DocumentId_Ordinal",
                table: "document_chunks",
                columns: new[] { "DocumentId", "Ordinal" });

            migrationBuilder.CreateIndex(
                name: "IX_documents_Collection_ContentHash",
                table: "documents",
                columns: new[] { "Collection", "ContentHash" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_documents_Collection_Status",
                table: "documents",
                columns: new[] { "Collection", "Status" });

            migrationBuilder.CreateIndex(
                name: "IX_eval_cases_Suite_Active",
                table: "eval_cases",
                columns: new[] { "Suite", "Active" });

            migrationBuilder.CreateIndex(
                name: "IX_eval_runs_Suite_StartedAt",
                table: "eval_runs",
                columns: new[] { "Suite", "StartedAt" });

            migrationBuilder.CreateIndex(
                name: "IX_feedback_ConversationId",
                table: "feedback",
                column: "ConversationId");

            migrationBuilder.CreateIndex(
                name: "IX_feedback_UserId_CreatedAt",
                table: "feedback",
                columns: new[] { "UserId", "CreatedAt" });

            migrationBuilder.CreateIndex(
                name: "IX_finetune_jobs_Status_CreatedAt",
                table: "finetune_jobs",
                columns: new[] { "Status", "CreatedAt" });

            migrationBuilder.CreateIndex(
                name: "IX_kanban_tasks_Column_UpdatedAt",
                table: "kanban_tasks",
                columns: new[] { "Column", "UpdatedAt" });

            migrationBuilder.CreateIndex(
                name: "IX_kanban_tasks_PatientRef",
                table: "kanban_tasks",
                column: "PatientRef");

            migrationBuilder.CreateIndex(
                name: "IX_kanban_tasks_UserId",
                table: "kanban_tasks",
                column: "UserId");

            migrationBuilder.CreateIndex(
                name: "IX_learned_skills_Intent_Reward",
                table: "learned_skills",
                columns: new[] { "Intent", "Reward" });

            migrationBuilder.CreateIndex(
                name: "IX_learned_skills_Signature",
                table: "learned_skills",
                column: "Signature");

            migrationBuilder.CreateIndex(
                name: "IX_preference_records_ConversationId",
                table: "preference_records",
                column: "ConversationId");

            migrationBuilder.CreateIndex(
                name: "IX_preference_records_CreatedAt_Specialty",
                table: "preference_records",
                columns: new[] { "CreatedAt", "Specialty" });

            migrationBuilder.CreateIndex(
                name: "IX_routing_stats_Intent_Provider_Model",
                table: "routing_stats",
                columns: new[] { "Intent", "Provider", "Model" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_session_summaries_UserId_PeriodEnd",
                table: "session_summaries",
                columns: new[] { "UserId", "PeriodEnd" });

            migrationBuilder.CreateIndex(
                name: "IX_shadow_comparisons_Intent_CreatedAt",
                table: "shadow_comparisons",
                columns: new[] { "Intent", "CreatedAt" });

            migrationBuilder.CreateIndex(
                name: "IX_tool_approval_requests_ConversationId",
                table: "tool_approval_requests",
                column: "ConversationId");

            migrationBuilder.CreateIndex(
                name: "IX_tool_approval_requests_Status_RequestedAt",
                table: "tool_approval_requests",
                columns: new[] { "Status", "RequestedAt" });
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "adversarial_patterns");

            migrationBuilder.DropTable(
                name: "agent_memories");

            migrationBuilder.DropTable(
                name: "audit_logs");

            migrationBuilder.DropTable(
                name: "challenger_configs");

            migrationBuilder.DropTable(
                name: "conversation_messages");

            migrationBuilder.DropTable(
                name: "conversation_summaries");

            migrationBuilder.DropTable(
                name: "document_chunks");

            migrationBuilder.DropTable(
                name: "eval_cases");

            migrationBuilder.DropTable(
                name: "eval_runs");

            migrationBuilder.DropTable(
                name: "feedback");

            migrationBuilder.DropTable(
                name: "finetune_jobs");

            migrationBuilder.DropTable(
                name: "kanban_tasks");

            migrationBuilder.DropTable(
                name: "learned_skills");

            migrationBuilder.DropTable(
                name: "preference_records");

            migrationBuilder.DropTable(
                name: "routing_stats");

            migrationBuilder.DropTable(
                name: "session_summaries");

            migrationBuilder.DropTable(
                name: "shadow_comparisons");

            migrationBuilder.DropTable(
                name: "tool_approval_requests");

            migrationBuilder.DropTable(
                name: "user_preferences");

            migrationBuilder.DropTable(
                name: "user_traits");

            migrationBuilder.DropTable(
                name: "conversations");

            migrationBuilder.DropTable(
                name: "documents");
        }
    }

