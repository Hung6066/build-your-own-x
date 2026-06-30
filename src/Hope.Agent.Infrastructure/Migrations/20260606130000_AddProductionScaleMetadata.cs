using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Hope.Agent.Infrastructure.Migrations;

/// <inheritdoc />
public partial class AddProductionScaleMetadata : Microsoft.EntityFrameworkCore.Migrations.Migration
{
    /// <inheritdoc />
    protected override void Up(MigrationBuilder migrationBuilder)
    {
        AddNullableUuid(migrationBuilder, "agent_memories", "TenantId");
        AddNullableUuid(migrationBuilder, "audit_logs", "TenantId");
        AddVersionColumns(migrationBuilder, "audit_logs", nullable: true);

        AddNullableUuid(migrationBuilder, "agent_decisions", "TenantId");
        AddVersionColumns(migrationBuilder, "agent_decisions");

        AddNullableUuid(migrationBuilder, "autonomous_actions", "TenantId");
        migrationBuilder.AddColumn<string>("IdempotencyKey", "autonomous_actions", "character varying(128)", maxLength: 128, nullable: true);
        migrationBuilder.AddColumn<string>("QueueBackend", "autonomous_actions", "character varying(64)", maxLength: 64, nullable: false, defaultValue: "postgres-ledger");
        migrationBuilder.AddColumn<bool>("DispatchedToDurableQueue", "autonomous_actions", "boolean", nullable: false, defaultValue: false);
        migrationBuilder.AddColumn<DateTimeOffset>("DispatchedAt", "autonomous_actions", "timestamp with time zone", nullable: true);
        migrationBuilder.AddColumn<string>("CompensationToolName", "autonomous_actions", "character varying(128)", maxLength: 128, nullable: true);
        migrationBuilder.AddColumn<string>("CompensationArgumentsJson", "autonomous_actions", "jsonb", nullable: true);
        AddVersionColumns(migrationBuilder, "autonomous_actions");

        AddNullableUuid(migrationBuilder, "autonomy_goals", "TenantId");
        AddNullableUuid(migrationBuilder, "autonomy_reflections", "TenantId");
        AddNullableUuid(migrationBuilder, "autonomy_learning_facts", "TenantId");

        AddNullableUuid(migrationBuilder, "autonomy_eval_gate_runs", "TenantId");
        AddVersionColumns(migrationBuilder, "autonomy_eval_gate_runs");

        AddNullableUuid(migrationBuilder, "eval_runs", "TenantId");
        AddVersionColumns(migrationBuilder, "eval_runs");

        migrationBuilder.CreateIndex("IX_agent_memories_TenantId_UserId_Kind", "agent_memories", new[] { "TenantId", "UserId", "Kind" });
        migrationBuilder.CreateIndex("IX_audit_logs_TenantId_OccurredAt", "audit_logs", new[] { "TenantId", "OccurredAt" });
        migrationBuilder.CreateIndex("IX_agent_decisions_TenantId_CreatedAt", "agent_decisions", new[] { "TenantId", "CreatedAt" });
        migrationBuilder.CreateIndex("IX_agent_decisions_TenantId_DecisionStatus_CreatedAt", "agent_decisions", new[] { "TenantId", "DecisionStatus", "CreatedAt" });
        migrationBuilder.CreateIndex("IX_autonomous_actions_IdempotencyKey", "autonomous_actions", "IdempotencyKey");
        migrationBuilder.CreateIndex("IX_autonomous_actions_TenantId_Status_ScheduledFor", "autonomous_actions", new[] { "TenantId", "Status", "ScheduledFor" });
        migrationBuilder.CreateIndex("IX_autonomy_goals_TenantId_CreatedAt", "autonomy_goals", new[] { "TenantId", "CreatedAt" });
        migrationBuilder.CreateIndex("IX_autonomy_reflections_TenantId_CreatedAt", "autonomy_reflections", new[] { "TenantId", "CreatedAt" });
        migrationBuilder.CreateIndex("IX_autonomy_learning_facts_TenantId_Kind_Key", "autonomy_learning_facts", new[] { "TenantId", "Kind", "Key" });
        migrationBuilder.CreateIndex("IX_autonomy_eval_gate_runs_TenantId_CreatedAt", "autonomy_eval_gate_runs", new[] { "TenantId", "CreatedAt" });
        migrationBuilder.CreateIndex("IX_eval_runs_TenantId_Suite_StartedAt", "eval_runs", new[] { "TenantId", "Suite", "StartedAt" });
    }

    /// <inheritdoc />
    protected override void Down(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.DropIndex("IX_agent_memories_TenantId_UserId_Kind", "agent_memories");
        migrationBuilder.DropIndex("IX_audit_logs_TenantId_OccurredAt", "audit_logs");
        migrationBuilder.DropIndex("IX_agent_decisions_TenantId_CreatedAt", "agent_decisions");
        migrationBuilder.DropIndex("IX_agent_decisions_TenantId_DecisionStatus_CreatedAt", "agent_decisions");
        migrationBuilder.DropIndex("IX_autonomous_actions_IdempotencyKey", "autonomous_actions");
        migrationBuilder.DropIndex("IX_autonomous_actions_TenantId_Status_ScheduledFor", "autonomous_actions");
        migrationBuilder.DropIndex("IX_autonomy_goals_TenantId_CreatedAt", "autonomy_goals");
        migrationBuilder.DropIndex("IX_autonomy_reflections_TenantId_CreatedAt", "autonomy_reflections");
        migrationBuilder.DropIndex("IX_autonomy_learning_facts_TenantId_Kind_Key", "autonomy_learning_facts");
        migrationBuilder.DropIndex("IX_autonomy_eval_gate_runs_TenantId_CreatedAt", "autonomy_eval_gate_runs");
        migrationBuilder.DropIndex("IX_eval_runs_TenantId_Suite_StartedAt", "eval_runs");

        DropVersionColumns(migrationBuilder, "eval_runs");
        migrationBuilder.DropColumn("TenantId", "eval_runs");

        DropVersionColumns(migrationBuilder, "autonomy_eval_gate_runs");
        migrationBuilder.DropColumn("TenantId", "autonomy_eval_gate_runs");

        migrationBuilder.DropColumn("TenantId", "autonomy_learning_facts");
        migrationBuilder.DropColumn("TenantId", "autonomy_reflections");
        migrationBuilder.DropColumn("TenantId", "autonomy_goals");

        DropVersionColumns(migrationBuilder, "autonomous_actions");
        migrationBuilder.DropColumn("CompensationArgumentsJson", "autonomous_actions");
        migrationBuilder.DropColumn("CompensationToolName", "autonomous_actions");
        migrationBuilder.DropColumn("DispatchedAt", "autonomous_actions");
        migrationBuilder.DropColumn("DispatchedToDurableQueue", "autonomous_actions");
        migrationBuilder.DropColumn("QueueBackend", "autonomous_actions");
        migrationBuilder.DropColumn("IdempotencyKey", "autonomous_actions");
        migrationBuilder.DropColumn("TenantId", "autonomous_actions");

        DropVersionColumns(migrationBuilder, "agent_decisions");
        migrationBuilder.DropColumn("TenantId", "agent_decisions");

        DropVersionColumns(migrationBuilder, "audit_logs");
        migrationBuilder.DropColumn("TenantId", "audit_logs");
        migrationBuilder.DropColumn("TenantId", "agent_memories");
    }

    private static void AddNullableUuid(MigrationBuilder migrationBuilder, string table, string column)
        => migrationBuilder.AddColumn<Guid>(column, table, "uuid", nullable: true);

    private static void AddVersionColumns(MigrationBuilder migrationBuilder, string table, bool nullable = false)
    {
        migrationBuilder.AddColumn<string>("DeploymentVersion", table, "character varying(128)", maxLength: 128, nullable: nullable, defaultValue: nullable ? null : "dev");
        migrationBuilder.AddColumn<string>("PromptVersion", table, "character varying(128)", maxLength: 128, nullable: nullable, defaultValue: nullable ? null : "hope-runtime-prompt-v1");
        migrationBuilder.AddColumn<string>("ModelVersion", table, "character varying(128)", maxLength: 128, nullable: nullable, defaultValue: nullable ? null : "unknown");
        migrationBuilder.AddColumn<string>("ToolsetVersion", table, "character varying(128)", maxLength: 128, nullable: nullable, defaultValue: nullable ? null : "hope-tools-v1");
        migrationBuilder.AddColumn<string>("PolicyVersion", table, "character varying(128)", maxLength: 128, nullable: nullable, defaultValue: nullable ? null : "hope-policy-v1");
    }

    private static void DropVersionColumns(MigrationBuilder migrationBuilder, string table)
    {
        migrationBuilder.DropColumn("PolicyVersion", table);
        migrationBuilder.DropColumn("ToolsetVersion", table);
        migrationBuilder.DropColumn("ModelVersion", table);
        migrationBuilder.DropColumn("PromptVersion", table);
        migrationBuilder.DropColumn("DeploymentVersion", table);
    }
}
