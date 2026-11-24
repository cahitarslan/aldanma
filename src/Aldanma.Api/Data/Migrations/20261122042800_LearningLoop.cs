using System;
using System.Collections.Generic;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Aldanma.Api.Data.Migrations
{
    /// <inheritdoc />
    public partial class LearningLoop : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "feedback",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    CreatedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    Correct = table.Column<bool>(type: "boolean", nullable: false),
                    Level = table.Column<string>(type: "character varying(16)", maxLength: 16, nullable: false),
                    Score = table.Column<int>(type: "integer", nullable: false),
                    RulesVersion = table.Column<string>(type: "character varying(20)", maxLength: 20, nullable: false),
                    Kinds = table.Column<List<string>>(type: "text[]", nullable: false),
                    Categories = table.Column<List<string>>(type: "text[]", nullable: false),
                    Signals = table.Column<List<string>>(type: "text[]", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_feedback", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "submissions",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    CreatedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    Text = table.Column<string>(type: "character varying(5000)", maxLength: 5000, nullable: false),
                    Sender = table.Column<string>(type: "character varying(16)", maxLength: 16, nullable: false),
                    Claim = table.Column<string>(type: "character varying(16)", maxLength: 16, nullable: false),
                    Level = table.Column<string>(type: "character varying(16)", maxLength: 16, nullable: false),
                    RulesVersion = table.Column<string>(type: "character varying(20)", maxLength: 20, nullable: false),
                    Status = table.Column<string>(type: "character varying(16)", maxLength: 16, nullable: false),
                    Label = table.Column<string>(type: "character varying(16)", maxLength: 16, nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_submissions", x => x.Id);
                });

            migrationBuilder.CreateIndex(
                name: "IX_feedback_CreatedAt",
                table: "feedback",
                column: "CreatedAt");

            migrationBuilder.CreateIndex(
                name: "IX_submissions_Status",
                table: "submissions",
                column: "Status");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "feedback");

            migrationBuilder.DropTable(
                name: "submissions");
        }
    }
}
