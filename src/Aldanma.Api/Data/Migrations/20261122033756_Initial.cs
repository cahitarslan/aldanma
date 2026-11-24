using System;
using Microsoft.EntityFrameworkCore.Migrations;
using Npgsql.EntityFrameworkCore.PostgreSQL.Metadata;

#nullable disable

namespace Aldanma.Api.Data.Migrations
{
    /// <inheritdoc />
    public partial class Initial : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "blocklist_entries",
                columns: table => new
                {
                    Id = table.Column<long>(type: "bigint", nullable: false)
                        .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityByDefaultColumn),
                    Source = table.Column<string>(type: "character varying(32)", maxLength: 32, nullable: false),
                    Kind = table.Column<string>(type: "character varying(16)", maxLength: 16, nullable: false),
                    Value = table.Column<string>(type: "character varying(300)", maxLength: 300, nullable: false),
                    FirstSeenAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    LastSeenAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    RemovedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_blocklist_entries", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "domain_registrations",
                columns: table => new
                {
                    Domain = table.Column<string>(type: "character varying(253)", maxLength: 253, nullable: false),
                    Found = table.Column<bool>(type: "boolean", nullable: false),
                    RegisteredAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    Registrar = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: true),
                    FetchedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_domain_registrations", x => x.Domain);
                });

            migrationBuilder.CreateTable(
                name: "identifiers",
                columns: table => new
                {
                    Id = table.Column<long>(type: "bigint", nullable: false)
                        .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityByDefaultColumn),
                    Kind = table.Column<string>(type: "character varying(16)", maxLength: 16, nullable: false),
                    KeyHash = table.Column<byte[]>(type: "bytea", nullable: false),
                    PublicValue = table.Column<string>(type: "character varying(300)", maxLength: 300, nullable: true),
                    Masked = table.Column<string>(type: "character varying(300)", maxLength: 300, nullable: false),
                    CreatedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    Hidden = table.Column<bool>(type: "boolean", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_identifiers", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "reports",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    CreatedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    Category = table.Column<string>(type: "character varying(32)", maxLength: 32, nullable: false),
                    Channel = table.Column<string>(type: "character varying(32)", maxLength: 32, nullable: false),
                    Loss = table.Column<string>(type: "character varying(32)", maxLength: 32, nullable: false),
                    OccurredOn = table.Column<DateOnly>(type: "date", nullable: true),
                    ReporterHash = table.Column<byte[]>(type: "bytea", nullable: false),
                    Status = table.Column<string>(type: "character varying(16)", maxLength: 16, nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_reports", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "disputes",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    IdentifierId = table.Column<long>(type: "bigint", nullable: false),
                    CreatedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    Contact = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: false),
                    Reason = table.Column<string>(type: "character varying(1000)", maxLength: 1000, nullable: false),
                    Status = table.Column<string>(type: "character varying(16)", maxLength: 16, nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_disputes", x => x.Id);
                    table.ForeignKey(
                        name: "FK_disputes_identifiers_IdentifierId",
                        column: x => x.IdentifierId,
                        principalTable: "identifiers",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "report_identifiers",
                columns: table => new
                {
                    ReportId = table.Column<Guid>(type: "uuid", nullable: false),
                    IdentifierId = table.Column<long>(type: "bigint", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_report_identifiers", x => new { x.ReportId, x.IdentifierId });
                    table.ForeignKey(
                        name: "FK_report_identifiers_identifiers_IdentifierId",
                        column: x => x.IdentifierId,
                        principalTable: "identifiers",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                    table.ForeignKey(
                        name: "FK_report_identifiers_reports_ReportId",
                        column: x => x.ReportId,
                        principalTable: "reports",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateIndex(
                name: "IX_blocklist_entries_Kind_Value",
                table: "blocklist_entries",
                columns: new[] { "Kind", "Value" });

            migrationBuilder.CreateIndex(
                name: "IX_blocklist_entries_Source_Kind_Value",
                table: "blocklist_entries",
                columns: new[] { "Source", "Kind", "Value" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_disputes_IdentifierId",
                table: "disputes",
                column: "IdentifierId");

            migrationBuilder.CreateIndex(
                name: "IX_disputes_Status",
                table: "disputes",
                column: "Status");

            migrationBuilder.CreateIndex(
                name: "IX_identifiers_Kind_KeyHash",
                table: "identifiers",
                columns: new[] { "Kind", "KeyHash" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_report_identifiers_IdentifierId",
                table: "report_identifiers",
                column: "IdentifierId");

            migrationBuilder.CreateIndex(
                name: "IX_reports_CreatedAt",
                table: "reports",
                column: "CreatedAt");

            migrationBuilder.CreateIndex(
                name: "IX_reports_ReporterHash",
                table: "reports",
                column: "ReporterHash");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "blocklist_entries");

            migrationBuilder.DropTable(
                name: "disputes");

            migrationBuilder.DropTable(
                name: "domain_registrations");

            migrationBuilder.DropTable(
                name: "report_identifiers");

            migrationBuilder.DropTable(
                name: "identifiers");

            migrationBuilder.DropTable(
                name: "reports");
        }
    }
}
