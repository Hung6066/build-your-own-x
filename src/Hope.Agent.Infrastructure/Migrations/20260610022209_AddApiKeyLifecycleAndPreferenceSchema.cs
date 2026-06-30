using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Hope.Agent.Infrastructure.Migrations;

/// <inheritdoc />
public partial class AddApiKeyLifecycleAndPreferenceSchema : Microsoft.EntityFrameworkCore.Migrations.Migration
{
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<string>(
                name: "Persona",
                table: "user_preferences",
                type: "character varying(64)",
                maxLength: 64,
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "PreferencesJson",
                table: "user_preferences",
                type: "jsonb",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "PreferredChannel",
                table: "user_preferences",
                type: "character varying(32)",
                maxLength: 32,
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "PreferredLanguage",
                table: "user_preferences",
                type: "character varying(16)",
                maxLength: 16,
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "Purpose",
                table: "user_preferences",
                type: "character varying(64)",
                maxLength: 64,
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "SafetyMode",
                table: "user_preferences",
                type: "character varying(64)",
                maxLength: 64,
                nullable: true);

            migrationBuilder.AddColumn<Guid>(
                name: "TenantId",
                table: "user_preferences",
                type: "uuid",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "UpdatedBy",
                table: "user_preferences",
                type: "character varying(128)",
                maxLength: 128,
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "Version",
                table: "user_preferences",
                type: "character varying(128)",
                maxLength: 128,
                nullable: true);

            migrationBuilder.CreateTable(
                name: "api_key_records",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    TenantId = table.Column<Guid>(type: "uuid", nullable: true),
                    Name = table.Column<string>(type: "character varying(128)", maxLength: 128, nullable: false),
                    Hash = table.Column<string>(type: "character varying(128)", maxLength: 128, nullable: false),
                    Scope = table.Column<string>(type: "character varying(256)", maxLength: 256, nullable: false),
                    Revoked = table.Column<bool>(type: "boolean", nullable: false),
                    ExpiresAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    CreatedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    RotatedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    RevokedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    CreatedBy = table.Column<string>(type: "character varying(128)", maxLength: 128, nullable: true),
                    RevokedBy = table.Column<string>(type: "character varying(128)", maxLength: 128, nullable: true),
                    Reason = table.Column<string>(type: "text", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_api_key_records", x => x.Id);
                });

            migrationBuilder.CreateIndex(
                name: "IX_user_preferences_TenantId_UpdatedAt",
                table: "user_preferences",
                columns: new[] { "TenantId", "UpdatedAt" });

            migrationBuilder.CreateIndex(
                name: "IX_api_key_records_Hash",
                table: "api_key_records",
                column: "Hash",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_api_key_records_TenantId_Revoked_ExpiresAt",
                table: "api_key_records",
                columns: new[] { "TenantId", "Revoked", "ExpiresAt" });
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "api_key_records");

            migrationBuilder.DropIndex(
                name: "IX_user_preferences_TenantId_UpdatedAt",
                table: "user_preferences");

            migrationBuilder.DropColumn(
                name: "Persona",
                table: "user_preferences");

            migrationBuilder.DropColumn(
                name: "PreferencesJson",
                table: "user_preferences");

            migrationBuilder.DropColumn(
                name: "PreferredChannel",
                table: "user_preferences");

            migrationBuilder.DropColumn(
                name: "PreferredLanguage",
                table: "user_preferences");

            migrationBuilder.DropColumn(
                name: "Purpose",
                table: "user_preferences");

            migrationBuilder.DropColumn(
                name: "SafetyMode",
                table: "user_preferences");

            migrationBuilder.DropColumn(
                name: "TenantId",
                table: "user_preferences");

            migrationBuilder.DropColumn(
                name: "UpdatedBy",
                table: "user_preferences");

            migrationBuilder.DropColumn(
                name: "Version",
                table: "user_preferences");
        }
}
