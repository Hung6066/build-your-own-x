using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Hope.Agent.Infrastructure.Migrations;

/// <inheritdoc />
public partial class AddAgentPersistenceTools : Microsoft.EntityFrameworkCore.Migrations.Migration
{
    /// <inheritdoc />
    protected override void Up(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.CreateTable(
            name: "medical_summaries",
            columns: table => new
            {
                Id = table.Column<Guid>(type: "uuid", nullable: false),
                SummaryId = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: false),
                PatientId = table.Column<Guid>(type: "uuid", nullable: true),
                UserId = table.Column<Guid>(type: "uuid", nullable: true),
                SummaryType = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: false),
                Audience = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: false),
                Specialty = table.Column<string>(type: "character varying(128)", maxLength: 128, nullable: true),
                SourceContext = table.Column<string>(type: "text", nullable: false),
                SummaryText = table.Column<string>(type: "text", nullable: false),
                Model = table.Column<string>(type: "character varying(128)", maxLength: 128, nullable: true),
                Status = table.Column<string>(type: "character varying(32)", maxLength: 32, nullable: false),
                CreatedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                UpdatedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                CorrelationId = table.Column<string>(type: "character varying(128)", maxLength: 128, nullable: true)
            },
            constraints: table => table.PrimaryKey("PK_medical_summaries", x => x.Id));

        migrationBuilder.CreateTable(
            name: "reminder_records",
            columns: table => new
            {
                Id = table.Column<Guid>(type: "uuid", nullable: false),
                ReminderId = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: false),
                PatientId = table.Column<Guid>(type: "uuid", nullable: false),
                UserId = table.Column<Guid>(type: "uuid", nullable: true),
                WorkflowId = table.Column<string>(type: "character varying(128)", maxLength: 128, nullable: true),
                ReminderType = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: false),
                MedicationName = table.Column<string>(type: "character varying(256)", maxLength: 256, nullable: false),
                Dosage = table.Column<string>(type: "character varying(128)", maxLength: 128, nullable: true),
                Frequency = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: false),
                StartAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                DurationDays = table.Column<int>(type: "integer", nullable: false),
                PreferredChannel = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: false),
                AdherenceRiskScore = table.Column<int>(type: "integer", nullable: false),
                Status = table.Column<string>(type: "character varying(32)", maxLength: 32, nullable: false),
                ConfirmedCount = table.Column<int>(type: "integer", nullable: false),
                MissedCount = table.Column<int>(type: "integer", nullable: false),
                LastConfirmedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                LastMissedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                EscalationReason = table.Column<string>(type: "text", nullable: true),
                CreatedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                UpdatedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                CorrelationId = table.Column<string>(type: "character varying(128)", maxLength: 128, nullable: true)
            },
            constraints: table => table.PrimaryKey("PK_reminder_records", x => x.Id));

        migrationBuilder.CreateTable(
            name: "audit_reports",
            columns: table => new
            {
                Id = table.Column<Guid>(type: "uuid", nullable: false),
                ReportId = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: false),
                RequestedBy = table.Column<Guid>(type: "uuid", nullable: false),
                ReportType = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: false),
                PeriodStart = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                PeriodEnd = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                Narrative = table.Column<string>(type: "text", nullable: false),
                MetricsJson = table.Column<string>(type: "jsonb", nullable: true),
                AnomaliesJson = table.Column<string>(type: "jsonb", nullable: true),
                Format = table.Column<string>(type: "character varying(16)", maxLength: 16, nullable: false),
                ExportPath = table.Column<string>(type: "character varying(512)", maxLength: 512, nullable: false),
                IntegrityHash = table.Column<string>(type: "character varying(128)", maxLength: 128, nullable: false),
                ByteSize = table.Column<int>(type: "integer", nullable: false),
                SigningAlgorithm = table.Column<string>(type: "character varying(32)", maxLength: 32, nullable: false),
                ExportedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                Status = table.Column<string>(type: "character varying(32)", maxLength: 32, nullable: false),
                CorrelationId = table.Column<string>(type: "character varying(128)", maxLength: 128, nullable: true)
            },
            constraints: table => table.PrimaryKey("PK_audit_reports", x => x.Id));

        migrationBuilder.CreateIndex("IX_medical_summaries_SummaryId", "medical_summaries", "SummaryId", unique: true);
        migrationBuilder.CreateIndex("IX_medical_summaries_PatientId_CreatedAt", "medical_summaries", new[] { "PatientId", "CreatedAt" });
        migrationBuilder.CreateIndex("IX_medical_summaries_UserId_CreatedAt", "medical_summaries", new[] { "UserId", "CreatedAt" });

        migrationBuilder.CreateIndex("IX_reminder_records_ReminderId", "reminder_records", "ReminderId", unique: true);
        migrationBuilder.CreateIndex("IX_reminder_records_WorkflowId", "reminder_records", "WorkflowId");
        migrationBuilder.CreateIndex("IX_reminder_records_PatientId_StartAt", "reminder_records", new[] { "PatientId", "StartAt" });
        migrationBuilder.CreateIndex("IX_reminder_records_UserId_UpdatedAt", "reminder_records", new[] { "UserId", "UpdatedAt" });

        migrationBuilder.CreateIndex("IX_audit_reports_ReportId", "audit_reports", "ReportId", unique: true);
        migrationBuilder.CreateIndex("IX_audit_reports_RequestedBy_ExportedAt", "audit_reports", new[] { "RequestedBy", "ExportedAt" });
        migrationBuilder.CreateIndex("IX_audit_reports_ReportType_PeriodEnd", "audit_reports", new[] { "ReportType", "PeriodEnd" });
    }

    /// <inheritdoc />
    protected override void Down(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.DropTable(name: "audit_reports");
        migrationBuilder.DropTable(name: "reminder_records");
        migrationBuilder.DropTable(name: "medical_summaries");
    }
}
