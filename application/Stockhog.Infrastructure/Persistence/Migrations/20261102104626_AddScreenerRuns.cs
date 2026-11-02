using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Stockhog.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class AddScreenerRuns : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "ScreenerRuns",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "TEXT", nullable: false),
                    CreatedAt = table.Column<DateTime>(type: "TEXT", nullable: false),
                    AsOf = table.Column<DateOnly>(type: "TEXT", nullable: false),
                    Criteria = table.Column<string>(type: "TEXT", maxLength: 2000, nullable: false),
                    UniverseSize = table.Column<int>(type: "INTEGER", nullable: false),
                    WithData = table.Column<int>(type: "INTEGER", nullable: false),
                    Matched = table.Column<int>(type: "INTEGER", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_ScreenerRuns", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "ScreenerRows",
                columns: table => new
                {
                    RunId = table.Column<Guid>(type: "TEXT", nullable: false),
                    Rank = table.Column<int>(type: "INTEGER", nullable: false),
                    Symbol = table.Column<string>(type: "TEXT", maxLength: 10, nullable: false),
                    Name = table.Column<string>(type: "TEXT", maxLength: 256, nullable: false),
                    Exchange = table.Column<string>(type: "TEXT", maxLength: 16, nullable: false),
                    LastDate = table.Column<DateOnly>(type: "TEXT", nullable: false),
                    LastClose = table.Column<decimal>(type: "TEXT", nullable: false),
                    AverageDollarVolume = table.Column<decimal>(type: "TEXT", nullable: false),
                    AtrPercent = table.Column<decimal>(type: "TEXT", nullable: false),
                    Return1MPercent = table.Column<decimal>(type: "TEXT", nullable: true),
                    Return6MPercent = table.Column<decimal>(type: "TEXT", nullable: true),
                    FromHighPercent = table.Column<decimal>(type: "TEXT", nullable: false),
                    TradingDays = table.Column<int>(type: "INTEGER", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_ScreenerRows", x => new { x.RunId, x.Rank });
                    table.ForeignKey(
                        name: "FK_ScreenerRows_ScreenerRuns_RunId",
                        column: x => x.RunId,
                        principalTable: "ScreenerRuns",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateIndex(
                name: "IX_ScreenerRuns_CreatedAt",
                table: "ScreenerRuns",
                column: "CreatedAt");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "ScreenerRows");

            migrationBuilder.DropTable(
                name: "ScreenerRuns");
        }
    }
}
