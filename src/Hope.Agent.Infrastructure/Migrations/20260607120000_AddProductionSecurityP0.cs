using Hope.Agent.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Hope.Agent.Infrastructure.Migrations;

/// <inheritdoc />
[Microsoft.EntityFrameworkCore.Infrastructure.DbContextAttribute(typeof(AgentDbContext))]
[Migration("20260607120000_AddProductionSecurityP0")]
public partial class AddProductionSecurityP0 : Microsoft.EntityFrameworkCore.Migrations.Migration
{
    /// <inheritdoc />
    protected override void Up(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.Sql("""
            CREATE TABLE IF NOT EXISTS security_posture_checks (
                "Id" uuid PRIMARY KEY DEFAULT gen_random_uuid(),
                "CheckName" varchar(128) NOT NULL UNIQUE,
                "RequiredState" text NOT NULL,
                "CurrentState" text NOT NULL DEFAULT 'configured',
                "Severity" varchar(32) NOT NULL DEFAULT 'P0',
                "UpdatedAt" timestamptz NOT NULL DEFAULT now()
            );
            """);

        migrationBuilder.Sql("""
            ALTER TABLE medical_summaries ADD COLUMN IF NOT EXISTS "TenantId" uuid;
            ALTER TABLE reminder_records ADD COLUMN IF NOT EXISTS "TenantId" uuid;
            ALTER TABLE appointment_bookings ADD COLUMN IF NOT EXISTS "TenantId" uuid;

            UPDATE audit_logs SET "TenantId" = '00000000-0000-0000-0000-000000000000' WHERE "TenantId" IS NULL;
            UPDATE agent_memories SET "TenantId" = '00000000-0000-0000-0000-000000000000' WHERE "TenantId" IS NULL;
            UPDATE agent_decisions SET "TenantId" = '00000000-0000-0000-0000-000000000000' WHERE "TenantId" IS NULL;
            UPDATE autonomous_actions SET "TenantId" = '00000000-0000-0000-0000-000000000000' WHERE "TenantId" IS NULL;
            UPDATE medical_summaries SET "TenantId" = '00000000-0000-0000-0000-000000000000' WHERE "TenantId" IS NULL;
            UPDATE reminder_records SET "TenantId" = '00000000-0000-0000-0000-000000000000' WHERE "TenantId" IS NULL;
            UPDATE appointment_bookings SET "TenantId" = '00000000-0000-0000-0000-000000000000' WHERE "TenantId" IS NULL;
            UPDATE outbox_events SET "TenantId" = '00000000-0000-0000-0000-000000000000' WHERE "TenantId" IS NULL;

            ALTER TABLE audit_logs ALTER COLUMN "TenantId" SET DEFAULT '00000000-0000-0000-0000-000000000000';
            ALTER TABLE agent_memories ALTER COLUMN "TenantId" SET DEFAULT '00000000-0000-0000-0000-000000000000';
            ALTER TABLE agent_decisions ALTER COLUMN "TenantId" SET DEFAULT '00000000-0000-0000-0000-000000000000';
            ALTER TABLE autonomous_actions ALTER COLUMN "TenantId" SET DEFAULT '00000000-0000-0000-0000-000000000000';
            ALTER TABLE medical_summaries ALTER COLUMN "TenantId" SET DEFAULT '00000000-0000-0000-0000-000000000000';
            ALTER TABLE reminder_records ALTER COLUMN "TenantId" SET DEFAULT '00000000-0000-0000-0000-000000000000';
            ALTER TABLE appointment_bookings ALTER COLUMN "TenantId" SET DEFAULT '00000000-0000-0000-0000-000000000000';
            ALTER TABLE outbox_events ALTER COLUMN "TenantId" SET DEFAULT '00000000-0000-0000-0000-000000000000';

            ALTER TABLE audit_logs ALTER COLUMN "TenantId" SET NOT NULL;
            ALTER TABLE agent_memories ALTER COLUMN "TenantId" SET NOT NULL;
            ALTER TABLE agent_decisions ALTER COLUMN "TenantId" SET NOT NULL;
            ALTER TABLE autonomous_actions ALTER COLUMN "TenantId" SET NOT NULL;
            ALTER TABLE medical_summaries ALTER COLUMN "TenantId" SET NOT NULL;
            ALTER TABLE reminder_records ALTER COLUMN "TenantId" SET NOT NULL;
            ALTER TABLE appointment_bookings ALTER COLUMN "TenantId" SET NOT NULL;
            ALTER TABLE outbox_events ALTER COLUMN "TenantId" SET NOT NULL;
            """);

        migrationBuilder.Sql("""CREATE INDEX IF NOT EXISTS "IX_medical_summaries_TenantId_PatientId_CreatedAt" ON medical_summaries ("TenantId", "PatientId", "CreatedAt" DESC);""");
        migrationBuilder.Sql("""CREATE INDEX IF NOT EXISTS "IX_reminder_records_TenantId_PatientId_StartAt" ON reminder_records ("TenantId", "PatientId", "StartAt" DESC);""");
        migrationBuilder.Sql("""CREATE INDEX IF NOT EXISTS "IX_appointment_bookings_TenantId_PatientId_ConfirmedAt" ON appointment_bookings ("TenantId", "PatientId", "ConfirmedAt" DESC);""");
        migrationBuilder.Sql("""CREATE INDEX IF NOT EXISTS "IX_agent_memories_TenantId_UserId_Kind" ON agent_memories ("TenantId", "UserId", "Kind");""");

        migrationBuilder.Sql("""
            CREATE OR REPLACE FUNCTION hope_current_tenant()
            RETURNS uuid
            LANGUAGE sql
            STABLE
            AS $$
                SELECT nullif(current_setting('app.tenant_id', true), '')::uuid
            $$;

            CREATE OR REPLACE FUNCTION hope_set_tenant_context(tenant_id uuid)
            RETURNS void
            LANGUAGE sql
            AS $$
                SELECT set_config('app.tenant_id', tenant_id::text, false)
            $$;
            """);

        foreach (var table in new[]
        {
            "audit_logs",
            "agent_memories",
            "agent_decisions",
            "autonomous_actions",
            "medical_summaries",
            "reminder_records",
            "appointment_bookings",
            "outbox_events",
        })
        {
            migrationBuilder.Sql($"""
                ALTER TABLE {table} ENABLE ROW LEVEL SECURITY;
                DROP POLICY IF EXISTS tenant_isolation_select ON {table};
                DROP POLICY IF EXISTS tenant_isolation_write ON {table};
                DROP POLICY IF EXISTS tenant_isolation_update ON {table};
                DROP POLICY IF EXISTS tenant_isolation_delete ON {table};
                CREATE POLICY tenant_isolation_select ON {table}
                    FOR SELECT USING ("TenantId" = hope_current_tenant());
                CREATE POLICY tenant_isolation_write ON {table}
                    FOR INSERT WITH CHECK ("TenantId" = hope_current_tenant());
                CREATE POLICY tenant_isolation_update ON {table}
                    FOR UPDATE USING ("TenantId" = hope_current_tenant()) WITH CHECK ("TenantId" = hope_current_tenant());
                CREATE POLICY tenant_isolation_delete ON {table}
                    FOR DELETE USING ("TenantId" = hope_current_tenant());
                """);
        }

        migrationBuilder.Sql("""
            INSERT INTO security_posture_checks ("CheckName", "RequiredState", "CurrentState")
            VALUES
              ('zero_trust_mtls', 'mTLS required for api/worker/kafka/postgres/redis/qdrant/temporal', 'configured-by-options'),
              ('workload_identity', 'workload identity required; no long-lived shared service secrets', 'configured-by-options'),
              ('postgres_rls', 'RLS enabled on PHI/memory/decision/action/audit tables', 'enabled'),
              ('tenant_id_not_null', 'TenantId NOT NULL on protected tables', 'enabled'),
              ('qdrant_tenant_payload', 'tenant_id payload required for vector memory points', 'configured-by-options'),
              ('redis_namespace_acl', 'Redis namespace and ACL required per environment', 'configured-by-options'),
              ('kms_envelope_encryption', 'KMS envelope encryption required for PHI/memory/audit payloads', 'configured-by-options'),
              ('audit_worm', 'audit hash-chain plus WORM archive and scheduled verification', 'configured-by-options'),
              ('dlp_external_channels', 'DLP redaction before Slack/Email/Zalo', 'enabled'),
              ('tool_default_deny', 'unknown tool/role/risk denied in production', 'configured-by-options')
            ON CONFLICT ("CheckName") DO UPDATE
            SET "RequiredState" = EXCLUDED."RequiredState",
                "CurrentState" = EXCLUDED."CurrentState",
                "UpdatedAt" = now();
            """);
    }

    /// <inheritdoc />
    protected override void Down(MigrationBuilder migrationBuilder)
    {
        foreach (var table in new[]
        {
            "audit_logs",
            "agent_memories",
            "agent_decisions",
            "autonomous_actions",
            "medical_summaries",
            "reminder_records",
            "appointment_bookings",
            "outbox_events",
        })
        {
            migrationBuilder.Sql($"ALTER TABLE {table} DISABLE ROW LEVEL SECURITY;");
        }

        migrationBuilder.Sql("""DROP FUNCTION IF EXISTS hope_set_tenant_context(uuid);""");
        migrationBuilder.Sql("""DROP FUNCTION IF EXISTS hope_current_tenant();""");
        migrationBuilder.Sql("""DROP TABLE IF EXISTS security_posture_checks;""");
        migrationBuilder.Sql("""ALTER TABLE medical_summaries DROP COLUMN IF EXISTS "TenantId";""");
        migrationBuilder.Sql("""ALTER TABLE reminder_records DROP COLUMN IF EXISTS "TenantId";""");
        migrationBuilder.Sql("""ALTER TABLE appointment_bookings DROP COLUMN IF EXISTS "TenantId";""");
    }
}
