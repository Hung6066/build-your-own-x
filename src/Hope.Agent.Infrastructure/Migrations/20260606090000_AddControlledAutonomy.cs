using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Hope.Agent.Infrastructure.Migrations;

/// <inheritdoc />
public partial class AddControlledAutonomy : Microsoft.EntityFrameworkCore.Migrations.Migration
{
    /// <inheritdoc />
    protected override void Up(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.CreateTable(
            name: "agent_decisions",
            columns: table => new
            {
                Id = table.Column<Guid>(type: "uuid", nullable: false),
                DecisionId = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: false),
                UserId = table.Column<Guid>(type: "uuid", nullable: false),
                PatientId = table.Column<Guid>(type: "uuid", nullable: true),
                ConversationId = table.Column<Guid>(type: "uuid", nullable: true),
                Intent = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: false),
                AgentProfile = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: true),
                InputSummary = table.Column<string>(type: "text", nullable: false),
                MemoryRefsJson = table.Column<string>(type: "jsonb", nullable: true),
                EvidenceJson = table.Column<string>(type: "jsonb", nullable: true),
                ProposedActionJson = table.Column<string>(type: "jsonb", nullable: true),
                RiskLevel = table.Column<int>(type: "integer", nullable: false),
                Confidence = table.Column<double>(type: "double precision", nullable: false),
                PolicyDecision = table.Column<int>(type: "integer", nullable: false),
                DecisionStatus = table.Column<int>(type: "integer", nullable: false),
                Reason = table.Column<string>(type: "character varying(512)", maxLength: 512, nullable: true),
                CreatedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                CorrelationId = table.Column<string>(type: "character varying(128)", maxLength: 128, nullable: true)
            },
            constraints: table => table.PrimaryKey("PK_agent_decisions", x => x.Id));

        migrationBuilder.CreateTable(
            name: "autonomous_actions",
            columns: table => new
            {
                Id = table.Column<Guid>(type: "uuid", nullable: false),
                ActionId = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: false),
                DecisionId = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: false),
                ToolName = table.Column<string>(type: "character varying(128)", maxLength: 128, nullable: false),
                ArgumentsJson = table.Column<string>(type: "jsonb", nullable: false),
                RiskLevel = table.Column<int>(type: "integer", nullable: false),
                Confidence = table.Column<double>(type: "double precision", nullable: false),
                Status = table.Column<int>(type: "integer", nullable: false),
                ScheduledFor = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                ExecutedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                ResultJson = table.Column<string>(type: "jsonb", nullable: true),
                Error = table.Column<string>(type: "text", nullable: true),
                AttemptCount = table.Column<int>(type: "integer", nullable: false),
                CreatedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                CorrelationId = table.Column<string>(type: "character varying(128)", maxLength: 128, nullable: true)
            },
            constraints: table => table.PrimaryKey("PK_autonomous_actions", x => x.Id));

        migrationBuilder.CreateIndex(
            name: "IX_agent_decisions_DecisionId",
            table: "agent_decisions",
            column: "DecisionId",
            unique: true);

        migrationBuilder.CreateIndex(
            name: "IX_agent_decisions_DecisionStatus_CreatedAt",
            table: "agent_decisions",
            columns: new[] { "DecisionStatus", "CreatedAt" });

        migrationBuilder.CreateIndex(
            name: "IX_agent_decisions_PatientId_CreatedAt",
            table: "agent_decisions",
            columns: new[] { "PatientId", "CreatedAt" });

        migrationBuilder.CreateIndex(
            name: "IX_agent_decisions_UserId_CreatedAt",
            table: "agent_decisions",
            columns: new[] { "UserId", "CreatedAt" });

        migrationBuilder.CreateIndex(
            name: "IX_autonomous_actions_ActionId",
            table: "autonomous_actions",
            column: "ActionId",
            unique: true);

        migrationBuilder.CreateIndex(
            name: "IX_autonomous_actions_DecisionId",
            table: "autonomous_actions",
            column: "DecisionId");

        migrationBuilder.CreateIndex(
            name: "IX_autonomous_actions_Status_ScheduledFor",
            table: "autonomous_actions",
            columns: new[] { "Status", "ScheduledFor" });

        migrationBuilder.CreateIndex(
            name: "IX_autonomous_actions_ToolName_CreatedAt",
            table: "autonomous_actions",
            columns: new[] { "ToolName", "CreatedAt" });
    }

    /// <inheritdoc />
    protected override void Down(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.DropTable(name: "autonomous_actions");
        migrationBuilder.DropTable(name: "agent_decisions");
    }
}
