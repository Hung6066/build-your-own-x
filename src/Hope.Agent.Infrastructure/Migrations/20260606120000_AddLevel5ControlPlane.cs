using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Hope.Agent.Infrastructure.Migrations;

/// <inheritdoc />
public partial class AddLevel5ControlPlane : Microsoft.EntityFrameworkCore.Migrations.Migration
{
    /// <inheritdoc />
    protected override void Up(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.CreateTable(
            name: "autonomy_eval_gate_runs",
            columns: table => new
            {
                Id = table.Column<Guid>(type: "uuid", nullable: false),
                GateId = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: false),
                SuiteName = table.Column<string>(type: "character varying(128)", maxLength: 128, nullable: false),
                Passed = table.Column<bool>(type: "boolean", nullable: false),
                PassRate = table.Column<double>(type: "double precision", nullable: false),
                MetricsJson = table.Column<string>(type: "jsonb", nullable: false),
                Reason = table.Column<string>(type: "character varying(512)", maxLength: 512, nullable: true),
                CreatedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                CorrelationId = table.Column<string>(type: "character varying(128)", maxLength: 128, nullable: true)
            },
            constraints: table => table.PrimaryKey("PK_autonomy_eval_gate_runs", x => x.Id));

        migrationBuilder.CreateTable(
            name: "autonomy_drift_signals",
            columns: table => new
            {
                Id = table.Column<Guid>(type: "uuid", nullable: false),
                SignalId = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: false),
                SignalType = table.Column<string>(type: "character varying(128)", maxLength: 128, nullable: false),
                Severity = table.Column<int>(type: "integer", nullable: false),
                Score = table.Column<double>(type: "double precision", nullable: false),
                BaselineJson = table.Column<string>(type: "jsonb", nullable: false),
                CurrentJson = table.Column<string>(type: "jsonb", nullable: false),
                Status = table.Column<int>(type: "integer", nullable: false),
                CreatedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                CorrelationId = table.Column<string>(type: "character varying(128)", maxLength: 128, nullable: true)
            },
            constraints: table => table.PrimaryKey("PK_autonomy_drift_signals", x => x.Id));

        migrationBuilder.CreateTable(
            name: "autonomy_compensations",
            columns: table => new
            {
                Id = table.Column<Guid>(type: "uuid", nullable: false),
                CompensationId = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: false),
                ActionId = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: false),
                ToolName = table.Column<string>(type: "character varying(128)", maxLength: 128, nullable: false),
                ArgumentsJson = table.Column<string>(type: "jsonb", nullable: false),
                Status = table.Column<int>(type: "integer", nullable: false),
                ResultJson = table.Column<string>(type: "jsonb", nullable: true),
                Error = table.Column<string>(type: "text", nullable: true),
                CreatedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                ExecutedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                CorrelationId = table.Column<string>(type: "character varying(128)", maxLength: 128, nullable: true)
            },
            constraints: table => table.PrimaryKey("PK_autonomy_compensations", x => x.Id));

        migrationBuilder.CreateTable(
            name: "autonomy_reviews",
            columns: table => new
            {
                Id = table.Column<Guid>(type: "uuid", nullable: false),
                ReviewId = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: false),
                DecisionId = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: false),
                ReviewerProfile = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: false),
                Verdict = table.Column<int>(type: "integer", nullable: false),
                Confidence = table.Column<double>(type: "double precision", nullable: false),
                Notes = table.Column<string>(type: "text", nullable: false),
                CreatedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                CorrelationId = table.Column<string>(type: "character varying(128)", maxLength: 128, nullable: true)
            },
            constraints: table => table.PrimaryKey("PK_autonomy_reviews", x => x.Id));

        migrationBuilder.CreateIndex(name: "IX_autonomy_eval_gate_runs_GateId", table: "autonomy_eval_gate_runs", column: "GateId", unique: true);
        migrationBuilder.CreateIndex(name: "IX_autonomy_eval_gate_runs_SuiteName_CreatedAt", table: "autonomy_eval_gate_runs", columns: new[] { "SuiteName", "CreatedAt" });
        migrationBuilder.CreateIndex(name: "IX_autonomy_eval_gate_runs_Passed_CreatedAt", table: "autonomy_eval_gate_runs", columns: new[] { "Passed", "CreatedAt" });
        migrationBuilder.CreateIndex(name: "IX_autonomy_drift_signals_SignalId", table: "autonomy_drift_signals", column: "SignalId", unique: true);
        migrationBuilder.CreateIndex(name: "IX_autonomy_drift_signals_Severity_CreatedAt", table: "autonomy_drift_signals", columns: new[] { "Severity", "CreatedAt" });
        migrationBuilder.CreateIndex(name: "IX_autonomy_drift_signals_SignalType_CreatedAt", table: "autonomy_drift_signals", columns: new[] { "SignalType", "CreatedAt" });
        migrationBuilder.CreateIndex(name: "IX_autonomy_compensations_CompensationId", table: "autonomy_compensations", column: "CompensationId", unique: true);
        migrationBuilder.CreateIndex(name: "IX_autonomy_compensations_ActionId", table: "autonomy_compensations", column: "ActionId");
        migrationBuilder.CreateIndex(name: "IX_autonomy_compensations_Status_CreatedAt", table: "autonomy_compensations", columns: new[] { "Status", "CreatedAt" });
        migrationBuilder.CreateIndex(name: "IX_autonomy_reviews_ReviewId", table: "autonomy_reviews", column: "ReviewId", unique: true);
        migrationBuilder.CreateIndex(name: "IX_autonomy_reviews_DecisionId", table: "autonomy_reviews", column: "DecisionId");
        migrationBuilder.CreateIndex(name: "IX_autonomy_reviews_Verdict_CreatedAt", table: "autonomy_reviews", columns: new[] { "Verdict", "CreatedAt" });
    }

    /// <inheritdoc />
    protected override void Down(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.DropTable(name: "autonomy_reviews");
        migrationBuilder.DropTable(name: "autonomy_compensations");
        migrationBuilder.DropTable(name: "autonomy_drift_signals");
        migrationBuilder.DropTable(name: "autonomy_eval_gate_runs");
    }
}
