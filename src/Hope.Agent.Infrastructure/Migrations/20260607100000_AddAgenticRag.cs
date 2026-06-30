using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Hope.Agent.Infrastructure.Migrations;

/// <inheritdoc />
public partial class AddAgenticRag : Microsoft.EntityFrameworkCore.Migrations.Migration
{
    /// <inheritdoc />
    protected override void Up(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.CreateTable(
            name: "agentic_rag_runs",
            columns: table => new
            {
                Id = table.Column<Guid>(type: "uuid", nullable: false),
                RunId = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: false),
                TenantId = table.Column<Guid>(type: "uuid", nullable: true),
                UserId = table.Column<Guid>(type: "uuid", nullable: false),
                PatientId = table.Column<Guid>(type: "uuid", nullable: true),
                ConversationId = table.Column<Guid>(type: "uuid", nullable: true),
                Query = table.Column<string>(type: "text", nullable: false),
                Answer = table.Column<string>(type: "text", nullable: false),
                Status = table.Column<int>(type: "integer", nullable: false),
                ContextSufficient = table.Column<bool>(type: "boolean", nullable: false),
                Confidence = table.Column<double>(type: "double precision", nullable: false),
                IterationCount = table.Column<int>(type: "integer", nullable: false),
                SelectedCorporaJson = table.Column<string>(type: "jsonb", nullable: false),
                CitationsJson = table.Column<string>(type: "jsonb", nullable: false),
                MetricsJson = table.Column<string>(type: "jsonb", nullable: false),
                CreatedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                CompletedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                CorrelationId = table.Column<string>(type: "character varying(128)", maxLength: 128, nullable: true)
            },
            constraints: table => table.PrimaryKey("PK_agentic_rag_runs", x => x.Id));

        migrationBuilder.CreateTable(
            name: "agentic_rag_steps",
            columns: table => new
            {
                Id = table.Column<Guid>(type: "uuid", nullable: false),
                StepId = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: false),
                RunId = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: false),
                Kind = table.Column<int>(type: "integer", nullable: false),
                Iteration = table.Column<int>(type: "integer", nullable: false),
                InputJson = table.Column<string>(type: "jsonb", nullable: false),
                OutputJson = table.Column<string>(type: "jsonb", nullable: false),
                CreatedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                CorrelationId = table.Column<string>(type: "character varying(128)", maxLength: 128, nullable: true)
            },
            constraints: table => table.PrimaryKey("PK_agentic_rag_steps", x => x.Id));

        migrationBuilder.CreateTable(
            name: "agentic_rag_retrievals",
            columns: table => new
            {
                Id = table.Column<Guid>(type: "uuid", nullable: false),
                RetrievalId = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: false),
                RunId = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: false),
                Iteration = table.Column<int>(type: "integer", nullable: false),
                Corpus = table.Column<string>(type: "character varying(128)", maxLength: 128, nullable: false),
                Query = table.Column<string>(type: "text", nullable: false),
                Source = table.Column<string>(type: "character varying(128)", maxLength: 128, nullable: false),
                ReferenceId = table.Column<string>(type: "character varying(128)", maxLength: 128, nullable: false),
                Title = table.Column<string>(type: "character varying(512)", maxLength: 512, nullable: false),
                Content = table.Column<string>(type: "text", nullable: false),
                Url = table.Column<string>(type: "character varying(1024)", maxLength: 1024, nullable: true),
                Score = table.Column<double>(type: "double precision", nullable: false),
                MetadataJson = table.Column<string>(type: "jsonb", nullable: false),
                CreatedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false)
            },
            constraints: table => table.PrimaryKey("PK_agentic_rag_retrievals", x => x.Id));

        migrationBuilder.CreateTable(
            name: "agentic_rag_context_assessments",
            columns: table => new
            {
                Id = table.Column<Guid>(type: "uuid", nullable: false),
                AssessmentId = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: false),
                RunId = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: false),
                Iteration = table.Column<int>(type: "integer", nullable: false),
                Sufficient = table.Column<bool>(type: "boolean", nullable: false),
                Confidence = table.Column<double>(type: "double precision", nullable: false),
                CoveredTermsJson = table.Column<string>(type: "jsonb", nullable: false),
                MissingTermsJson = table.Column<string>(type: "jsonb", nullable: false),
                Feedback = table.Column<string>(type: "text", nullable: false),
                CreatedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false)
            },
            constraints: table => table.PrimaryKey("PK_agentic_rag_context_assessments", x => x.Id));

        migrationBuilder.CreateIndex("IX_agentic_rag_runs_RunId", "agentic_rag_runs", "RunId", unique: true);
        migrationBuilder.CreateIndex("IX_agentic_rag_runs_TenantId_CreatedAt", "agentic_rag_runs", new[] { "TenantId", "CreatedAt" });
        migrationBuilder.CreateIndex("IX_agentic_rag_runs_PatientId_CreatedAt", "agentic_rag_runs", new[] { "PatientId", "CreatedAt" });
        migrationBuilder.CreateIndex("IX_agentic_rag_runs_Status_CreatedAt", "agentic_rag_runs", new[] { "Status", "CreatedAt" });
        migrationBuilder.CreateIndex("IX_agentic_rag_steps_StepId", "agentic_rag_steps", "StepId", unique: true);
        migrationBuilder.CreateIndex("IX_agentic_rag_steps_RunId_Iteration_CreatedAt", "agentic_rag_steps", new[] { "RunId", "Iteration", "CreatedAt" });
        migrationBuilder.CreateIndex("IX_agentic_rag_retrievals_RetrievalId", "agentic_rag_retrievals", "RetrievalId", unique: true);
        migrationBuilder.CreateIndex("IX_agentic_rag_retrievals_RunId_Corpus", "agentic_rag_retrievals", new[] { "RunId", "Corpus" });
        migrationBuilder.CreateIndex("IX_agentic_rag_retrievals_Source_ReferenceId", "agentic_rag_retrievals", new[] { "Source", "ReferenceId" });
        migrationBuilder.CreateIndex("IX_agentic_rag_context_assessments_AssessmentId", "agentic_rag_context_assessments", "AssessmentId", unique: true);
        migrationBuilder.CreateIndex("IX_agentic_rag_context_assessments_RunId_Iteration", "agentic_rag_context_assessments", new[] { "RunId", "Iteration" });
        migrationBuilder.CreateIndex("IX_agentic_rag_context_assessments_Sufficient_CreatedAt", "agentic_rag_context_assessments", new[] { "Sufficient", "CreatedAt" });
    }

    /// <inheritdoc />
    protected override void Down(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.DropTable("agentic_rag_context_assessments");
        migrationBuilder.DropTable("agentic_rag_retrievals");
        migrationBuilder.DropTable("agentic_rag_steps");
        migrationBuilder.DropTable("agentic_rag_runs");
    }
}
