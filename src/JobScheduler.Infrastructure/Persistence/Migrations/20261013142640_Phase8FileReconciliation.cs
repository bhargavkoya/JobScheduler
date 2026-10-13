using System;
using Microsoft.EntityFrameworkCore.Migrations;
using Npgsql.EntityFrameworkCore.PostgreSQL.Metadata;

#nullable disable

namespace JobScheduler.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class Phase8FileReconciliation : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<bool>(
                name: "IsFileReconciliation",
                table: "JobTemplates",
                type: "boolean",
                nullable: false,
                defaultValue: false);

            migrationBuilder.CreateTable(
                name: "InputFiles",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    OwnerId = table.Column<Guid>(type: "uuid", nullable: false),
                    JobId = table.Column<Guid>(type: "uuid", nullable: true),
                    Alias = table.Column<string>(type: "character varying(30)", maxLength: 30, nullable: false),
                    FileName = table.Column<string>(type: "character varying(260)", maxLength: 260, nullable: false),
                    SizeBytes = table.Column<long>(type: "bigint", nullable: false),
                    Sha256 = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: false),
                    HeadersJson = table.Column<string>(type: "jsonb", nullable: false),
                    RowCount = table.Column<int>(type: "integer", nullable: false),
                    UploadedAtUtc = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    SupersededAtUtc = table.Column<DateTime>(type: "timestamp with time zone", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_InputFiles", x => x.Id);
                    table.ForeignKey(
                        name: "FK_InputFiles_Jobs_JobId",
                        column: x => x.JobId,
                        principalTable: "Jobs",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                    table.ForeignKey(
                        name: "FK_InputFiles_Users_OwnerId",
                        column: x => x.OwnerId,
                        principalTable: "Users",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "ReconciliationRules",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    JobId = table.Column<Guid>(type: "uuid", nullable: false),
                    Position = table.Column<int>(type: "integer", nullable: false),
                    Name = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: false),
                    LeftAlias = table.Column<string>(type: "character varying(30)", maxLength: 30, nullable: false),
                    RightAlias = table.Column<string>(type: "character varying(30)", maxLength: 30, nullable: false),
                    LeftKeyColumn = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: true),
                    RightKeyColumn = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: true),
                    LeftExpression = table.Column<string>(type: "character varying(2000)", maxLength: 2000, nullable: false),
                    Operator = table.Column<string>(type: "character varying(32)", maxLength: 32, nullable: false),
                    RightExpression = table.Column<string>(type: "character varying(2000)", maxLength: 2000, nullable: false),
                    Tolerance = table.Column<decimal>(type: "numeric(28,10)", precision: 28, scale: 10, nullable: false),
                    ToleranceMode = table.Column<string>(type: "character varying(32)", maxLength: 32, nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_ReconciliationRules", x => x.Id);
                    table.ForeignKey(
                        name: "FK_ReconciliationRules_Jobs_JobId",
                        column: x => x.JobId,
                        principalTable: "Jobs",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "InputFileContents",
                columns: table => new
                {
                    FileId = table.Column<Guid>(type: "uuid", nullable: false),
                    Bytes = table.Column<byte[]>(type: "bytea", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_InputFileContents", x => x.FileId);
                    table.ForeignKey(
                        name: "FK_InputFileContents_InputFiles_FileId",
                        column: x => x.FileId,
                        principalTable: "InputFiles",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "ReconciliationResults",
                columns: table => new
                {
                    Id = table.Column<long>(type: "bigint", nullable: false)
                        .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityByDefaultColumn),
                    RunId = table.Column<Guid>(type: "uuid", nullable: false),
                    RuleId = table.Column<Guid>(type: "uuid", nullable: false),
                    RuleName = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: false),
                    RecordKey = table.Column<string>(type: "character varying(500)", maxLength: 500, nullable: false),
                    LeftRow = table.Column<int>(type: "integer", nullable: true),
                    RightRow = table.Column<int>(type: "integer", nullable: true),
                    LeftValue = table.Column<decimal>(type: "numeric(28,10)", precision: 28, scale: 10, nullable: true),
                    RightValue = table.Column<decimal>(type: "numeric(28,10)", precision: 28, scale: 10, nullable: true),
                    Difference = table.Column<decimal>(type: "numeric(28,10)", precision: 28, scale: 10, nullable: true),
                    Status = table.Column<string>(type: "character varying(16)", maxLength: 16, nullable: false),
                    Reason = table.Column<string>(type: "character varying(1000)", maxLength: 1000, nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_ReconciliationResults", x => x.Id);
                    table.ForeignKey(
                        name: "FK_ReconciliationResults_JobRuns_RunId",
                        column: x => x.RunId,
                        principalTable: "JobRuns",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                    table.ForeignKey(
                        name: "FK_ReconciliationResults_ReconciliationRules_RuleId",
                        column: x => x.RuleId,
                        principalTable: "ReconciliationRules",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateIndex(
                name: "IX_InputFiles_JobId_Alias",
                table: "InputFiles",
                columns: new[] { "JobId", "Alias" });

            migrationBuilder.CreateIndex(
                name: "IX_InputFiles_OwnerId",
                table: "InputFiles",
                column: "OwnerId");

            migrationBuilder.CreateIndex(
                name: "IX_ReconciliationResults_RuleId",
                table: "ReconciliationResults",
                column: "RuleId");

            migrationBuilder.CreateIndex(
                name: "IX_ReconciliationResults_RunId_RuleId_Status",
                table: "ReconciliationResults",
                columns: new[] { "RunId", "RuleId", "Status" });

            migrationBuilder.CreateIndex(
                name: "IX_ReconciliationRules_JobId_Position",
                table: "ReconciliationRules",
                columns: new[] { "JobId", "Position" });
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "InputFileContents");

            migrationBuilder.DropTable(
                name: "ReconciliationResults");

            migrationBuilder.DropTable(
                name: "InputFiles");

            migrationBuilder.DropTable(
                name: "ReconciliationRules");

            migrationBuilder.DropColumn(
                name: "IsFileReconciliation",
                table: "JobTemplates");
        }
    }
}
