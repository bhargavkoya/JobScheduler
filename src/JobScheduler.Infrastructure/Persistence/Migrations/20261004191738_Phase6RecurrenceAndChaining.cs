using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace JobScheduler.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class Phase6RecurrenceAndChaining : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<string>(
                name: "RecurrenceCron",
                table: "Jobs",
                type: "text",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "RecurrenceText",
                table: "Jobs",
                type: "text",
                nullable: true);

            migrationBuilder.AddColumn<Guid>(
                name: "TriggerJobId",
                table: "Jobs",
                type: "uuid",
                nullable: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "RecurrenceCron",
                table: "Jobs");

            migrationBuilder.DropColumn(
                name: "RecurrenceText",
                table: "Jobs");

            migrationBuilder.DropColumn(
                name: "TriggerJobId",
                table: "Jobs");
        }
    }
}
