using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Hope.Agent.Infrastructure.Migrations;

/// <inheritdoc />
public partial class AddEnterpriseSecurityP2 : Microsoft.EntityFrameworkCore.Migrations.Migration
{
    /// <inheritdoc />
    protected override void Up(MigrationBuilder migrationBuilder)
    {
            migrationBuilder.CreateTable(
                name: "adversarial_simulation_runs",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    TenantId = table.Column<Guid>(type: "uuid", nullable: true),
                    SimulationId = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: false),
                    TargetEnvironment = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: false),
                    SuitesJson = table.Column<string>(type: "jsonb", nullable: false),
                    ReplayAgainstCanary = table.Column<bool>(type: "boolean", nullable: false),
                    PassRate = table.Column<double>(type: "double precision", nullable: false),
                    Passed = table.Column<bool>(type: "boolean", nullable: false),
                    FindingsJson = table.Column<string>(type: "jsonb", nullable: false),
                    PolicyVersion = table.Column<string>(type: "character varying(128)", maxLength: 128, nullable: false),
                    CreatedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    CorrelationId = table.Column<string>(type: "character varying(128)", maxLength: 128, nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_adversarial_simulation_runs", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "break_glass_access_records",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    TenantId = table.Column<Guid>(type: "uuid", nullable: false),
                    ActorUserId = table.Column<Guid>(type: "uuid", nullable: false),
                    Purpose = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: false),
                    Reason = table.Column<string>(type: "text", nullable: false),
                    Status = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: false),
                    CreatedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    ReviewDueAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    ReviewedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    Reviewer = table.Column<string>(type: "character varying(128)", maxLength: 128, nullable: true),
                    CorrelationId = table.Column<string>(type: "character varying(128)", maxLength: 128, nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_break_glass_access_records", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "context_provenance_records",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    TenantId = table.Column<Guid>(type: "uuid", nullable: true),
                    PatientId = table.Column<Guid>(type: "uuid", nullable: true),
                    ConversationId = table.Column<Guid>(type: "uuid", nullable: true),
                    DecisionId = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: true),
                    ActionId = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: true),
                    CorrelationId = table.Column<string>(type: "character varying(128)", maxLength: 128, nullable: false),
                    AnswerHash = table.Column<string>(type: "character varying(96)", maxLength: 96, nullable: false),
                    RetrievalQuery = table.Column<string>(type: "text", nullable: false),
                    SourceManifestJson = table.Column<string>(type: "jsonb", nullable: false),
                    DroppedContextJson = table.Column<string>(type: "jsonb", nullable: false),
                    TokenBudget = table.Column<int>(type: "integer", nullable: false),
                    Purpose = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: false),
                    Sensitivity = table.Column<string>(type: "character varying(32)", maxLength: 32, nullable: false),
                    PolicyVersion = table.Column<string>(type: "character varying(128)", maxLength: 128, nullable: false),
                    CreatedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_context_provenance_records", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "security_incidents",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    TenantId = table.Column<Guid>(type: "uuid", nullable: true),
                    IncidentType = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: false),
                    Severity = table.Column<string>(type: "character varying(32)", maxLength: 32, nullable: false),
                    Status = table.Column<string>(type: "character varying(32)", maxLength: 32, nullable: false),
                    Summary = table.Column<string>(type: "text", nullable: false),
                    AgentProfile = table.Column<string>(type: "character varying(128)", maxLength: 128, nullable: true),
                    ToolName = table.Column<string>(type: "character varying(128)", maxLength: 128, nullable: true),
                    AutonomyDisabled = table.Column<bool>(type: "boolean", nullable: false),
                    ToolDisabled = table.Column<bool>(type: "boolean", nullable: false),
                    RunbookJson = table.Column<string>(type: "jsonb", nullable: false),
                    ForensicExportJson = table.Column<string>(type: "jsonb", nullable: true),
                    CreatedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    ClosedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    CorrelationId = table.Column<string>(type: "character varying(128)", maxLength: 128, nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_security_incidents", x => x.Id);
                });

            migrationBuilder.CreateIndex(
                name: "IX_adversarial_simulation_runs_Passed_CreatedAt",
                table: "adversarial_simulation_runs",
                columns: new[] { "Passed", "CreatedAt" });

            migrationBuilder.CreateIndex(
                name: "IX_adversarial_simulation_runs_SimulationId",
                table: "adversarial_simulation_runs",
                column: "SimulationId",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_adversarial_simulation_runs_TenantId_CreatedAt",
                table: "adversarial_simulation_runs",
                columns: new[] { "TenantId", "CreatedAt" });

            migrationBuilder.CreateIndex(
                name: "IX_break_glass_access_records_ActorUserId",
                table: "break_glass_access_records",
                column: "ActorUserId");

            migrationBuilder.CreateIndex(
                name: "IX_break_glass_access_records_TenantId_Status_ReviewDueAt",
                table: "break_glass_access_records",
                columns: new[] { "TenantId", "Status", "ReviewDueAt" });

            migrationBuilder.CreateIndex(
                name: "IX_context_provenance_records_CorrelationId",
                table: "context_provenance_records",
                column: "CorrelationId");

            migrationBuilder.CreateIndex(
                name: "IX_context_provenance_records_DecisionId",
                table: "context_provenance_records",
                column: "DecisionId");

            migrationBuilder.CreateIndex(
                name: "IX_context_provenance_records_TenantId_PatientId_CreatedAt",
                table: "context_provenance_records",
                columns: new[] { "TenantId", "PatientId", "CreatedAt" });

            migrationBuilder.CreateIndex(
                name: "IX_security_incidents_CorrelationId",
                table: "security_incidents",
                column: "CorrelationId");

            migrationBuilder.CreateIndex(
                name: "IX_security_incidents_TenantId_Status_CreatedAt",
                table: "security_incidents",
                columns: new[] { "TenantId", "Status", "CreatedAt" });
    }

    /// <inheritdoc />
    protected override void Down(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.DropTable(
            name: "adversarial_simulation_runs");

        migrationBuilder.DropTable(
            name: "break_glass_access_records");

        migrationBuilder.DropTable(
            name: "context_provenance_records");

        migrationBuilder.DropTable(
            name: "security_incidents");
    }
}
