using System;
using Hope.Agent.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Hope.Agent.Infrastructure.Migrations;

/// <inheritdoc />
[Microsoft.EntityFrameworkCore.Infrastructure.DbContextAttribute(typeof(AgentDbContext))]
[Migration("20260607110000_AddDatabaseScaleOptimizations")]
public partial class AddDatabaseScaleOptimizations : Microsoft.EntityFrameworkCore.Migrations.Migration
{
    /// <inheritdoc />
    protected override void Up(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.Sql("""CREATE EXTENSION IF NOT EXISTS pg_trgm;""");
        migrationBuilder.Sql("""CREATE EXTENSION IF NOT EXISTS pgcrypto;""");

        migrationBuilder.CreateTable(
            name: "outbox_events",
            columns: table => new
            {
                Id = table.Column<Guid>(type: "uuid", nullable: false),
                TenantId = table.Column<Guid>(type: "uuid", nullable: true),
                Topic = table.Column<string>(type: "character varying(256)", maxLength: 256, nullable: false),
                Key = table.Column<string>(type: "character varying(256)", maxLength: 256, nullable: false),
                PayloadJson = table.Column<string>(type: "jsonb", nullable: false),
                HeadersJson = table.Column<string>(type: "jsonb", nullable: false),
                Status = table.Column<int>(type: "integer", nullable: false),
                AttemptCount = table.Column<int>(type: "integer", nullable: false),
                MaxAttempts = table.Column<int>(type: "integer", nullable: false),
                ScheduledFor = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                PublishedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                LastError = table.Column<string>(type: "text", nullable: true),
                CorrelationId = table.Column<string>(type: "character varying(128)", maxLength: 128, nullable: true),
                IdempotencyKey = table.Column<string>(type: "character varying(256)", maxLength: 256, nullable: true),
                CreatedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                UpdatedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false)
            },
            constraints: table => table.PrimaryKey("PK_outbox_events", x => x.Id));

        migrationBuilder.CreateTable(
            name: "agent_ops_hourly_metrics",
            columns: table => new
            {
                Id = table.Column<Guid>(type: "uuid", nullable: false),
                TenantId = table.Column<Guid>(type: "uuid", nullable: true),
                AgentProfile = table.Column<string>(type: "character varying(128)", maxLength: 128, nullable: false),
                HourBucket = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                AgentRuns = table.Column<long>(type: "bigint", nullable: false),
                ToolCalls = table.Column<long>(type: "bigint", nullable: false),
                ToolFailures = table.Column<long>(type: "bigint", nullable: false),
                Decisions = table.Column<long>(type: "bigint", nullable: false),
                ActionsQueued = table.Column<long>(type: "bigint", nullable: false),
                ActionsSucceeded = table.Column<long>(type: "bigint", nullable: false),
                ActionsFailed = table.Column<long>(type: "bigint", nullable: false),
                LatencyP95Ms = table.Column<double>(type: "double precision", nullable: false),
                CostUsd = table.Column<decimal>(type: "numeric(18,6)", precision: 18, scale: 6, nullable: false),
                UpdatedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false)
            },
            constraints: table => table.PrimaryKey("PK_agent_ops_hourly_metrics", x => x.Id));

        migrationBuilder.CreateTable(
            name: "tenant_cost_daily",
            columns: table => new
            {
                Id = table.Column<Guid>(type: "uuid", nullable: false),
                TenantId = table.Column<Guid>(type: "uuid", nullable: true),
                DayBucket = table.Column<DateOnly>(type: "date", nullable: false),
                AgentProfile = table.Column<string>(type: "character varying(128)", maxLength: 128, nullable: false),
                Model = table.Column<string>(type: "character varying(128)", maxLength: 128, nullable: false),
                Runs = table.Column<long>(type: "bigint", nullable: false),
                CostUsd = table.Column<decimal>(type: "numeric(18,6)", precision: 18, scale: 6, nullable: false),
                InputTokens = table.Column<long>(type: "bigint", nullable: false),
                OutputTokens = table.Column<long>(type: "bigint", nullable: false),
                UpdatedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false)
            },
            constraints: table => table.PrimaryKey("PK_tenant_cost_daily", x => x.Id));

        migrationBuilder.CreateTable(
            name: "workflow_success_daily",
            columns: table => new
            {
                Id = table.Column<Guid>(type: "uuid", nullable: false),
                TenantId = table.Column<Guid>(type: "uuid", nullable: true),
                DayBucket = table.Column<DateOnly>(type: "date", nullable: false),
                WorkflowName = table.Column<string>(type: "character varying(128)", maxLength: 128, nullable: false),
                Started = table.Column<long>(type: "bigint", nullable: false),
                Succeeded = table.Column<long>(type: "bigint", nullable: false),
                Failed = table.Column<long>(type: "bigint", nullable: false),
                SuccessRate = table.Column<double>(type: "double precision", nullable: false),
                LatencyP95Ms = table.Column<double>(type: "double precision", nullable: false),
                UpdatedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false)
            },
            constraints: table => table.PrimaryKey("PK_workflow_success_daily", x => x.Id));

        migrationBuilder.CreateTable(
            name: "scale_partition_policies",
            columns: table => new
            {
                Id = table.Column<Guid>(type: "uuid", nullable: false),
                TableName = table.Column<string>(type: "character varying(128)", maxLength: 128, nullable: false),
                PartitionKey = table.Column<string>(type: "character varying(128)", maxLength: 128, nullable: false),
                Strategy = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: false),
                HotRetentionDays = table.Column<int>(type: "integer", nullable: false),
                ArchiveAfterDays = table.Column<int>(type: "integer", nullable: false),
                Enabled = table.Column<bool>(type: "boolean", nullable: false),
                UpdatedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false)
            },
            constraints: table => table.PrimaryKey("PK_scale_partition_policies", x => x.Id));

        migrationBuilder.CreateIndex("IX_outbox_events_Status_ScheduledFor", "outbox_events", new[] { "Status", "ScheduledFor" });
        migrationBuilder.CreateIndex("IX_outbox_events_TenantId_Status_ScheduledFor", "outbox_events", new[] { "TenantId", "Status", "ScheduledFor" });
        migrationBuilder.CreateIndex("IX_outbox_events_Topic_CreatedAt", "outbox_events", new[] { "Topic", "CreatedAt" });
        migrationBuilder.CreateIndex("IX_outbox_events_IdempotencyKey", "outbox_events", "IdempotencyKey");
        migrationBuilder.CreateIndex("IX_agent_ops_hourly_metrics_TenantId_HourBucket_AgentProfile", "agent_ops_hourly_metrics", new[] { "TenantId", "HourBucket", "AgentProfile" }, unique: true);
        migrationBuilder.CreateIndex("IX_agent_ops_hourly_metrics_HourBucket", "agent_ops_hourly_metrics", "HourBucket");
        migrationBuilder.CreateIndex("IX_tenant_cost_daily_TenantId_DayBucket_AgentProfile_Model", "tenant_cost_daily", new[] { "TenantId", "DayBucket", "AgentProfile", "Model" }, unique: true);
        migrationBuilder.CreateIndex("IX_tenant_cost_daily_DayBucket", "tenant_cost_daily", "DayBucket");
        migrationBuilder.CreateIndex("IX_workflow_success_daily_TenantId_DayBucket_WorkflowName", "workflow_success_daily", new[] { "TenantId", "DayBucket", "WorkflowName" }, unique: true);
        migrationBuilder.CreateIndex("IX_workflow_success_daily_DayBucket", "workflow_success_daily", "DayBucket");
        migrationBuilder.CreateIndex("IX_scale_partition_policies_TableName", "scale_partition_policies", "TableName", unique: true);

        migrationBuilder.Sql("""CREATE INDEX IF NOT EXISTS "IX_audit_logs_TenantId_Action_OccurredAt" ON audit_logs ("TenantId", "Action", "OccurredAt" DESC);""");
        migrationBuilder.Sql("""CREATE INDEX IF NOT EXISTS "IX_agent_decisions_TenantId_PatientId_CreatedAt" ON agent_decisions ("TenantId", "PatientId", "CreatedAt" DESC);""");
        migrationBuilder.Sql("""CREATE INDEX IF NOT EXISTS "IX_autonomous_actions_TenantId_Status_ScheduledFor" ON autonomous_actions ("TenantId", "Status", "ScheduledFor");""");
        migrationBuilder.Sql("""CREATE INDEX IF NOT EXISTS "IX_agentic_rag_retrievals_RunId_Iteration_CreatedAt" ON agentic_rag_retrievals ("RunId", "Iteration", "CreatedAt");""");

        migrationBuilder.Sql("""CREATE INDEX IF NOT EXISTS "IX_medical_summaries_SummaryText_Fts" ON medical_summaries USING gin (to_tsvector('simple', coalesce("SummaryText", '')));""");
        migrationBuilder.Sql("""CREATE INDEX IF NOT EXISTS "IX_medical_summaries_SummaryText_Trgm" ON medical_summaries USING gin ("SummaryText" gin_trgm_ops);""");
        migrationBuilder.Sql("""CREATE INDEX IF NOT EXISTS "IX_reminder_records_Fts" ON reminder_records USING gin (to_tsvector('simple', coalesce("ReminderType",'') || ' ' || coalesce("MedicationName",'') || ' ' || coalesce("Dosage",'') || ' ' || coalesce("Frequency",'') || ' ' || coalesce("Status",'') || ' ' || coalesce("EscalationReason",'')));""");
        migrationBuilder.Sql("""CREATE INDEX IF NOT EXISTS "IX_audit_logs_PayloadJson_Fts" ON audit_logs USING gin (to_tsvector('simple', coalesce("PayloadJson"::text, '')));""");
        migrationBuilder.Sql("""CREATE INDEX IF NOT EXISTS "IX_conversation_messages_Content_Fts" ON conversation_messages USING gin (to_tsvector('simple', coalesce("Content", '')));""");
        migrationBuilder.Sql("""CREATE INDEX IF NOT EXISTS "IX_conversation_messages_Content_Trgm" ON conversation_messages USING gin ("Content" gin_trgm_ops);""");

        migrationBuilder.Sql("""
            INSERT INTO scale_partition_policies ("Id", "TableName", "PartitionKey", "Strategy", "HotRetentionDays", "ArchiveAfterDays", "Enabled", "UpdatedAt")
            VALUES
              (gen_random_uuid(), 'audit_logs', 'OccurredAt', 'monthly-time-tenant', 365, 2555, true, now()),
              (gen_random_uuid(), 'agent_decisions', 'CreatedAt', 'monthly-time-tenant', 180, 2555, true, now()),
              (gen_random_uuid(), 'autonomous_actions', 'CreatedAt', 'monthly-time-tenant', 180, 2555, true, now()),
              (gen_random_uuid(), 'agentic_rag_retrievals', 'CreatedAt', 'monthly-time-runid', 90, 365, true, now()),
              (gen_random_uuid(), 'agentic_rag_steps', 'CreatedAt', 'monthly-time-runid', 90, 365, true, now())
            ON CONFLICT ("TableName") DO UPDATE
            SET "PartitionKey" = EXCLUDED."PartitionKey",
                "Strategy" = EXCLUDED."Strategy",
                "HotRetentionDays" = EXCLUDED."HotRetentionDays",
                "ArchiveAfterDays" = EXCLUDED."ArchiveAfterDays",
                "Enabled" = EXCLUDED."Enabled",
                "UpdatedAt" = now();
            """);

        migrationBuilder.Sql("""
            CREATE OR REPLACE FUNCTION hope_ensure_scale_partitions(months_ahead integer DEFAULT 3)
            RETURNS void
            LANGUAGE plpgsql
            AS $$
            DECLARE
                policy record;
                month_start date;
                month_end date;
                child_name text;
                i integer;
                parent_oid oid;
            BEGIN
                FOR policy IN SELECT * FROM scale_partition_policies WHERE "Enabled" = true LOOP
                    parent_oid := to_regclass(policy."TableName");
                    IF parent_oid IS NULL OR NOT EXISTS (SELECT 1 FROM pg_partitioned_table WHERE partrelid = parent_oid) THEN
                        CONTINUE;
                    END IF;

                    FOR i IN 0..GREATEST(months_ahead, 1) LOOP
                        month_start := date_trunc('month', now())::date + (i || ' months')::interval;
                        month_end := month_start + interval '1 month';
                        child_name := format('%s_%s', policy."TableName", to_char(month_start, 'YYYY_MM'));
                        EXECUTE format(
                            'CREATE TABLE IF NOT EXISTS %I PARTITION OF %I FOR VALUES FROM (%L) TO (%L)',
                            child_name, policy."TableName", month_start, month_end);
                    END LOOP;
                END LOOP;
            END;
            $$;
            """);
    }

    /// <inheritdoc />
    protected override void Down(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.Sql("""DROP FUNCTION IF EXISTS hope_ensure_scale_partitions(integer);""");
        migrationBuilder.DropTable("scale_partition_policies");
        migrationBuilder.DropTable("workflow_success_daily");
        migrationBuilder.DropTable("tenant_cost_daily");
        migrationBuilder.DropTable("agent_ops_hourly_metrics");
        migrationBuilder.DropTable("outbox_events");
        migrationBuilder.Sql("""DROP INDEX IF EXISTS "IX_conversation_messages_Content_Trgm";""");
        migrationBuilder.Sql("""DROP INDEX IF EXISTS "IX_conversation_messages_Content_Fts";""");
        migrationBuilder.Sql("""DROP INDEX IF EXISTS "IX_audit_logs_PayloadJson_Fts";""");
        migrationBuilder.Sql("""DROP INDEX IF EXISTS "IX_reminder_records_Fts";""");
        migrationBuilder.Sql("""DROP INDEX IF EXISTS "IX_medical_summaries_SummaryText_Trgm";""");
        migrationBuilder.Sql("""DROP INDEX IF EXISTS "IX_medical_summaries_SummaryText_Fts";""");
        migrationBuilder.Sql("""DROP INDEX IF EXISTS "IX_agentic_rag_retrievals_RunId_Iteration_CreatedAt";""");
        migrationBuilder.Sql("""DROP INDEX IF EXISTS "IX_agent_decisions_TenantId_PatientId_CreatedAt";""");
        migrationBuilder.Sql("""DROP INDEX IF EXISTS "IX_audit_logs_TenantId_Action_OccurredAt";""");
    }
}
