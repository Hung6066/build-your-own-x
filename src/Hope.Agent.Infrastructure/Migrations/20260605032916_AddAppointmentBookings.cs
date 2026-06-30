using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Hope.Agent.Infrastructure.Migrations;

/// <inheritdoc />
public partial class AddAppointmentBookings : Microsoft.EntityFrameworkCore.Migrations.Migration
{
    /// <inheritdoc />
    protected override void Up(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.CreateTable(
            name: "appointment_bookings",
            columns: table => new
            {
                Id = table.Column<Guid>(type: "uuid", nullable: false),
                BookingId = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: false),
                PatientId = table.Column<Guid>(type: "uuid", nullable: true),
                UserId = table.Column<Guid>(type: "uuid", nullable: true),
                DoctorId = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: false),
                SlotId = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: false),
                Reason = table.Column<string>(type: "text", nullable: true),
                AppointmentTime = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                Status = table.Column<string>(type: "character varying(32)", maxLength: 32, nullable: false),
                ConfirmedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                CorrelationId = table.Column<string>(type: "character varying(128)", maxLength: 128, nullable: true)
            },
            constraints: table =>
            {
                table.PrimaryKey("PK_appointment_bookings", x => x.Id);
            });

        migrationBuilder.CreateIndex(
            name: "IX_appointment_bookings_BookingId",
            table: "appointment_bookings",
            column: "BookingId",
            unique: true);

        migrationBuilder.CreateIndex(
            name: "IX_appointment_bookings_PatientId_ConfirmedAt",
            table: "appointment_bookings",
            columns: new[] { "PatientId", "ConfirmedAt" });

        migrationBuilder.CreateIndex(
            name: "IX_appointment_bookings_UserId_ConfirmedAt",
            table: "appointment_bookings",
            columns: new[] { "UserId", "ConfirmedAt" });
    }

    /// <inheritdoc />
    protected override void Down(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.DropTable(
            name: "appointment_bookings");
    }
}
