using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Hope.Agent.Infrastructure.Migrations;

/// <inheritdoc />
public partial class AddOptimizationCostHints : Microsoft.EntityFrameworkCore.Migrations.Migration
{
    /// <inheritdoc />
    protected override void Up(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.CreateTable(
            name: "optimization_cost_hints",
            columns: table => new
            {
                Id = table.Column<Guid>(type: "uuid", nullable: false),
                DoctorId = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: false),
                Specialty = table.Column<string>(type: "character varying(128)", maxLength: 128, nullable: false),
                SuccessRate = table.Column<double>(type: "double precision", nullable: false),
                Samples = table.Column<long>(type: "bigint", nullable: false),
                UpdatedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false)
            },
            constraints: table => table.PrimaryKey("PK_optimization_cost_hints", x => x.Id));

        migrationBuilder.CreateIndex(
            name: "IX_optimization_cost_hints_DoctorId_Specialty",
            table: "optimization_cost_hints",
            columns: new[] { "DoctorId", "Specialty" },
            unique: true);
    }

    /// <inheritdoc />
    protected override void Down(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.DropTable(name: "optimization_cost_hints");
    }
}
