using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace JobScheduler.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class AddJobTemplatesAndJobs : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "JobTemplates",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    Name = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: false),
                    Description = table.Column<string>(type: "character varying(2000)", maxLength: 2000, nullable: false),
                    SupportedScheduleTypes = table.Column<int[]>(type: "integer[]", nullable: false),
                    DefaultRetryPolicy_MaxAutoRetries = table.Column<int>(type: "integer", nullable: false),
                    DefaultRetryPolicy_BackoffSeconds = table.Column<int>(type: "integer", nullable: false),
                    IsApproved = table.Column<bool>(type: "boolean", nullable: false),
                    CreatedAtUtc = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    Fields = table.Column<string>(type: "jsonb", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_JobTemplates", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "Jobs",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    TemplateId = table.Column<Guid>(type: "uuid", nullable: false),
                    Name = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: false),
                    ScheduleType = table.Column<string>(type: "character varying(32)", maxLength: 32, nullable: false),
                    RunAtUtc = table.Column<DateTime>(type: "timestamp with time zone", nullable: true),
                    ConfigJson = table.Column<string>(type: "jsonb", nullable: false),
                    RetryPolicy_MaxAutoRetries = table.Column<int>(type: "integer", nullable: false),
                    RetryPolicy_BackoffSeconds = table.Column<int>(type: "integer", nullable: false),
                    OwnerId = table.Column<Guid>(type: "uuid", nullable: false),
                    Team = table.Column<string>(type: "character varying(32)", maxLength: 32, nullable: false),
                    Status = table.Column<string>(type: "character varying(32)", maxLength: 32, nullable: false),
                    CreatedAtUtc = table.Column<DateTime>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_Jobs", x => x.Id);
                    table.ForeignKey(
                        name: "FK_Jobs_JobTemplates_TemplateId",
                        column: x => x.TemplateId,
                        principalTable: "JobTemplates",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_Jobs_Users_OwnerId",
                        column: x => x.OwnerId,
                        principalTable: "Users",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateIndex(
                name: "IX_Jobs_OwnerId",
                table: "Jobs",
                column: "OwnerId");

            migrationBuilder.CreateIndex(
                name: "IX_Jobs_Status",
                table: "Jobs",
                column: "Status");

            migrationBuilder.CreateIndex(
                name: "IX_Jobs_Team_CreatedAtUtc",
                table: "Jobs",
                columns: new[] { "Team", "CreatedAtUtc" });

            migrationBuilder.CreateIndex(
                name: "IX_Jobs_TemplateId",
                table: "Jobs",
                column: "TemplateId");

            migrationBuilder.CreateIndex(
                name: "IX_JobTemplates_Name",
                table: "JobTemplates",
                column: "Name",
                unique: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "Jobs");

            migrationBuilder.DropTable(
                name: "JobTemplates");
        }
    }
}
