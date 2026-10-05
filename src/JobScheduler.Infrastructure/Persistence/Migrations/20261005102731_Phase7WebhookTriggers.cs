using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace JobScheduler.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class Phase7WebhookTriggers : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<string>(
                name: "WebhookTokenHash",
                table: "Jobs",
                type: "character varying(64)",
                maxLength: 64,
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "TriggerPayload",
                table: "JobRuns",
                type: "character varying(8192)",
                maxLength: 8192,
                nullable: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "WebhookTokenHash",
                table: "Jobs");

            migrationBuilder.DropColumn(
                name: "TriggerPayload",
                table: "JobRuns");
        }
    }
}
