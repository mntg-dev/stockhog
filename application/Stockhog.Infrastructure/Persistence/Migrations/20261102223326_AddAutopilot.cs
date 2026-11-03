using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Stockhog.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class AddAutopilot : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "Autopilot",
                columns: table => new
                {
                    Id = table.Column<string>(type: "TEXT", maxLength: 32, nullable: false),
                    Mode = table.Column<string>(type: "TEXT", maxLength: 8, nullable: false),
                    MaxConsecutiveLosses = table.Column<int>(type: "INTEGER", nullable: false),
                    MaxCapitalPercent = table.Column<decimal>(type: "TEXT", nullable: false),
                    LastCycleSession = table.Column<DateOnly>(type: "TEXT", nullable: true),
                    LastCycleAt = table.Column<DateTime>(type: "TEXT", nullable: true),
                    LastAssignmentId = table.Column<int>(type: "INTEGER", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_Autopilot", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "AutopilotDecisions",
                columns: table => new
                {
                    Id = table.Column<long>(type: "INTEGER", nullable: false)
                        .Annotation("Sqlite:Autoincrement", true),
                    Time = table.Column<DateTime>(type: "TEXT", nullable: false),
                    Mode = table.Column<string>(type: "TEXT", maxLength: 8, nullable: false),
                    AssignmentId = table.Column<int>(type: "INTEGER", nullable: false),
                    Symbol = table.Column<string>(type: "TEXT", maxLength: 10, nullable: false),
                    StrategyName = table.Column<string>(type: "TEXT", maxLength: 64, nullable: false),
                    Action = table.Column<string>(type: "TEXT", maxLength: 8, nullable: false),
                    Quantity = table.Column<decimal>(type: "TEXT", nullable: false),
                    Price = table.Column<decimal>(type: "TEXT", nullable: true),
                    StopPrice = table.Column<decimal>(type: "TEXT", nullable: true),
                    Reason = table.Column<string>(type: "TEXT", maxLength: 1000, nullable: false),
                    Executed = table.Column<bool>(type: "INTEGER", nullable: false),
                    OrderId = table.Column<string>(type: "TEXT", maxLength: 128, nullable: true),
                    Profit = table.Column<decimal>(type: "TEXT", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_AutopilotDecisions", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "AutopilotAssignments",
                columns: table => new
                {
                    Id = table.Column<int>(type: "INTEGER", nullable: false),
                    AutopilotId = table.Column<string>(type: "TEXT", maxLength: 32, nullable: false),
                    Symbol = table.Column<string>(type: "TEXT", maxLength: 10, nullable: false),
                    StrategyName = table.Column<string>(type: "TEXT", maxLength: 64, nullable: false),
                    Score = table.Column<decimal>(type: "TEXT", nullable: true),
                    AddedAt = table.Column<DateTime>(type: "TEXT", nullable: false),
                    Enabled = table.Column<bool>(type: "INTEGER", nullable: false),
                    PausedReason = table.Column<string>(type: "TEXT", maxLength: 500, nullable: true),
                    Quantity = table.Column<decimal>(type: "TEXT", nullable: false),
                    EntryPrice = table.Column<decimal>(type: "TEXT", nullable: true),
                    EntryAt = table.Column<DateTime>(type: "TEXT", nullable: true),
                    StopPrice = table.Column<decimal>(type: "TEXT", nullable: true),
                    RealizedProfit = table.Column<decimal>(type: "TEXT", nullable: false),
                    ClosedTrades = table.Column<int>(type: "INTEGER", nullable: false),
                    WinningTrades = table.Column<int>(type: "INTEGER", nullable: false),
                    ConsecutiveLosses = table.Column<int>(type: "INTEGER", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_AutopilotAssignments", x => new { x.AutopilotId, x.Id });
                    table.ForeignKey(
                        name: "FK_AutopilotAssignments_Autopilot_AutopilotId",
                        column: x => x.AutopilotId,
                        principalTable: "Autopilot",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateIndex(
                name: "IX_AutopilotDecisions_Time",
                table: "AutopilotDecisions",
                column: "Time");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "AutopilotAssignments");

            migrationBuilder.DropTable(
                name: "AutopilotDecisions");

            migrationBuilder.DropTable(
                name: "Autopilot");
        }
    }
}
