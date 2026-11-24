using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Aldanma.Api.Data.Migrations
{
    /// <inheritdoc />
    public partial class ResumableBlocklistImport : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "blocklist_import_runs",
                columns: table => new
                {
                    Source = table.Column<string>(type: "character varying(32)", maxLength: 32, nullable: false),
                    Mode = table.Column<string>(type: "character varying(16)", maxLength: 16, nullable: false),
                    StartedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    Since = table.Column<DateOnly>(type: "date", nullable: true),
                    TypeIndex = table.Column<int>(type: "integer", nullable: false),
                    NextPage = table.Column<int>(type: "integer", nullable: false),
                    UpdatedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_blocklist_import_runs", x => x.Source);
                });
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "blocklist_import_runs");
        }
    }
}
