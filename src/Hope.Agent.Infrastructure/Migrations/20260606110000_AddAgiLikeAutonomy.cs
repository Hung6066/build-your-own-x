using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Hope.Agent.Infrastructure.Migrations;

/// <inheritdoc />
public partial class AddAgiLikeAutonomy : Microsoft.EntityFrameworkCore.Migrations.Migration
{
    /// <inheritdoc />
    protected override void Up(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.CreateTable(
            name: "autonomy_goals",
            columns: table => new
            {
                Id = table.Column<Guid>(type: "uuid", nullable: false),
                GoalId = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: false),
                PatientId = table.Column<Guid>(type: "uuid", nullable: true),
                UserId = table.Column<Guid>(type: "uuid", nullable: false),
                GoalType = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: false),
                Description = table.Column<string>(type: "text", nullable: false),
                EvidenceJson = table.Column<string>(type: "jsonb", nullable: false),
                PriorityScore = table.Column<double>(type: "double precision", nullable: false),
                Confidence = table.Column<double>(type: "double precision", nullable: false),
                MaxAllowedRisk = table.Column<int>(type: "integer", nullable: false),
                Status = table.Column<int>(type: "integer", nullable: false),
                DecisionId = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: true),
                Reason = table.Column<string>(type: "character varying(512)", maxLength: 512, nullable: true),
                CreatedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                CompletedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                CorrelationId = table.Column<string>(type: "character varying(128)", maxLength: 128, nullable: true)
            },
            constraints: table => table.PrimaryKey("PK_autonomy_goals", x => x.Id));

        migrationBuilder.CreateTable(
            name: "autonomy_reflections",
            columns: table => new
            {
                Id = table.Column<Guid>(type: "uuid", nullable: false),
                ReflectionId = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: false),
                GoalId = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: true),
                DecisionId = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: true),
                ActionId = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: true),
                PatientId = table.Column<Guid>(type: "uuid", nullable: true),
                Succeeded = table.Column<bool>(type: "boolean", nullable: false),
                Summary = table.Column<string>(type: "text", nullable: false),
                LessonsJson = table.Column<string>(type: "jsonb", nullable: false),
                ConfidenceDelta = table.Column<double>(type: "double precision", nullable: false),
                CorrelationId = table.Column<string>(type: "character varying(128)", maxLength: 128, nullable: true),
                CreatedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false)
            },
            constraints: table => table.PrimaryKey("PK_autonomy_reflections", x => x.Id));

        migrationBuilder.CreateTable(
            name: "autonomy_learning_facts",
            columns: table => new
            {
                Id = table.Column<Guid>(type: "uuid", nullable: false),
                FactId = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: false),
                Kind = table.Column<int>(type: "integer", nullable: false),
                Key = table.Column<string>(type: "character varying(256)", maxLength: 256, nullable: false),
                ValueJson = table.Column<string>(type: "jsonb", nullable: false),
                Confidence = table.Column<double>(type: "double precision", nullable: false),
                Source = table.Column<string>(type: "character varying(128)", maxLength: 128, nullable: false),
                CreatedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                LastObservedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true)
            },
            constraints: table => table.PrimaryKey("PK_autonomy_learning_facts", x => x.Id));

        migrationBuilder.CreateIndex(name: "IX_autonomy_goals_GoalId", table: "autonomy_goals", column: "GoalId", unique: true);
        migrationBuilder.CreateIndex(name: "IX_autonomy_goals_PatientId_CreatedAt", table: "autonomy_goals", columns: new[] { "PatientId", "CreatedAt" });
        migrationBuilder.CreateIndex(name: "IX_autonomy_goals_Status_CreatedAt", table: "autonomy_goals", columns: new[] { "Status", "CreatedAt" });
        migrationBuilder.CreateIndex(name: "IX_autonomy_goals_DecisionId", table: "autonomy_goals", column: "DecisionId");
        migrationBuilder.CreateIndex(name: "IX_autonomy_reflections_ReflectionId", table: "autonomy_reflections", column: "ReflectionId", unique: true);
        migrationBuilder.CreateIndex(name: "IX_autonomy_reflections_PatientId_CreatedAt", table: "autonomy_reflections", columns: new[] { "PatientId", "CreatedAt" });
        migrationBuilder.CreateIndex(name: "IX_autonomy_reflections_ActionId", table: "autonomy_reflections", column: "ActionId");
        migrationBuilder.CreateIndex(name: "IX_autonomy_learning_facts_FactId", table: "autonomy_learning_facts", column: "FactId", unique: true);
        migrationBuilder.CreateIndex(name: "IX_autonomy_learning_facts_Kind_Key", table: "autonomy_learning_facts", columns: new[] { "Kind", "Key" }, unique: true);
        migrationBuilder.CreateIndex(name: "IX_autonomy_learning_facts_LastObservedAt", table: "autonomy_learning_facts", column: "LastObservedAt");
    }

    /// <inheritdoc />
    protected override void Down(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.DropTable(name: "autonomy_learning_facts");
        migrationBuilder.DropTable(name: "autonomy_reflections");
        migrationBuilder.DropTable(name: "autonomy_goals");
    }
}
