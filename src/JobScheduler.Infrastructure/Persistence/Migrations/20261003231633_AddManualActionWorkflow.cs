using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace JobScheduler.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class AddManualActionWorkflow : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<bool>(
                name: "RequiresApproval",
                table: "JobTemplates",
                type: "boolean",
                nullable: false,
                defaultValue: false);

            migrationBuilder.AddColumn<Guid>(
                name: "ApproverUserId",
                table: "Jobs",
                type: "uuid",
                nullable: true);

            migrationBuilder.AddColumn<DateTime>(
                name: "StatusChangedAtUtc",
                table: "Jobs",
                type: "timestamp with time zone",
                nullable: false,
                defaultValue: new DateTime(1, 1, 1, 0, 0, 0, 0, DateTimeKind.Unspecified));

            // Existing jobs: treat the status as having started at creation, not at year 1 (would flag everything at risk).
            migrationBuilder.Sql("UPDATE \"Jobs\" SET \"StatusChangedAtUtc\" = \"CreatedAtUtc\"");

            migrationBuilder.CreateTable(
                name: "JobApprovals",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    JobId = table.Column<Guid>(type: "uuid", nullable: false),
                    RunId = table.Column<Guid>(type: "uuid", nullable: true),
                    ApproverUserId = table.Column<Guid>(type: "uuid", nullable: false),
                    Decision = table.Column<string>(type: "character varying(32)", maxLength: 32, nullable: false),
                    Comment = table.Column<string>(type: "character varying(2000)", maxLength: 2000, nullable: false),
                    DecidedAtUtc = table.Column<DateTime>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_JobApprovals", x => x.Id);
                    table.ForeignKey(
                        name: "FK_JobApprovals_Jobs_JobId",
                        column: x => x.JobId,
                        principalTable: "Jobs",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                    table.ForeignKey(
                        name: "FK_JobApprovals_Users_ApproverUserId",
                        column: x => x.ApproverUserId,
                        principalTable: "Users",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateIndex(
                name: "IX_Jobs_ApproverUserId",
                table: "Jobs",
                column: "ApproverUserId");

            migrationBuilder.CreateIndex(
                name: "IX_JobApprovals_ApproverUserId",
                table: "JobApprovals",
                column: "ApproverUserId");

            migrationBuilder.CreateIndex(
                name: "IX_JobApprovals_JobId",
                table: "JobApprovals",
                column: "JobId");

            migrationBuilder.AddForeignKey(
                name: "FK_Jobs_Users_ApproverUserId",
                table: "Jobs",
                column: "ApproverUserId",
                principalTable: "Users",
                principalColumn: "Id",
                onDelete: ReferentialAction.Restrict);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropForeignKey(
                name: "FK_Jobs_Users_ApproverUserId",
                table: "Jobs");

            migrationBuilder.DropTable(
                name: "JobApprovals");

            migrationBuilder.DropIndex(
                name: "IX_Jobs_ApproverUserId",
                table: "Jobs");

            migrationBuilder.DropColumn(
                name: "RequiresApproval",
                table: "JobTemplates");

            migrationBuilder.DropColumn(
                name: "ApproverUserId",
                table: "Jobs");

            migrationBuilder.DropColumn(
                name: "StatusChangedAtUtc",
                table: "Jobs");
        }
    }
}
