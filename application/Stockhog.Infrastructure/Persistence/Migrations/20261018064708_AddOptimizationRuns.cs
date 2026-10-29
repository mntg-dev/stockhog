using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Stockhog.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class AddOptimizationRuns : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<string>(
                name: "OptimizationRunId",
                table: "BacktestRuns",
                type: "TEXT",
                maxLength: 128,
                nullable: true);

            migrationBuilder.CreateTable(
                name: "OptimizationRuns",
                columns: table => new
                {
                    Id = table.Column<string>(type: "TEXT", maxLength: 128, nullable: false),
                    Symbol = table.Column<string>(type: "TEXT", maxLength: 10, nullable: false),
                    StrategyName = table.Column<string>(type: "TEXT", maxLength: 64, nullable: false),
                    Grid = table.Column<string>(type: "TEXT", maxLength: 2000, nullable: false),
                    Objective = table.Column<string>(type: "TEXT", maxLength: 32, nullable: false),
                    MaxParallelTrials = table.Column<int>(type: "INTEGER", nullable: false),
                    Status = table.Column<string>(type: "TEXT", maxLength: 16, nullable: false),
                    CreatedAt = table.Column<DateTime>(type: "TEXT", nullable: false),
                    StartedAt = table.Column<DateTime>(type: "TEXT", nullable: true),
                    FinishedAt = table.Column<DateTime>(type: "TEXT", nullable: true),
                    FailureReason = table.Column<string>(type: "TEXT", maxLength: 2000, nullable: true),
                    BestTrialIndex = table.Column<int>(type: "INTEGER", nullable: true),
                    InitialCapital = table.Column<decimal>(type: "TEXT", nullable: false),
                    Currency = table.Column<string>(type: "TEXT", maxLength: 3, nullable: false),
                    PeriodEnd = table.Column<DateOnly>(type: "TEXT", nullable: false),
                    PeriodStart = table.Column<DateOnly>(type: "TEXT", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_OptimizationRuns", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "OptimizationTrials",
                columns: table => new
                {
                    Index = table.Column<int>(type: "INTEGER", nullable: false),
                    OptimizationRunId = table.Column<string>(type: "TEXT", maxLength: 128, nullable: false),
                    Parameters = table.Column<string>(type: "TEXT", maxLength: 4000, nullable: false),
                    Status = table.Column<string>(type: "TEXT", maxLength: 16, nullable: false),
                    RunId = table.Column<string>(type: "TEXT", maxLength: 128, nullable: true),
                    Metrics = table.Column<string>(type: "TEXT", maxLength: 1000, nullable: true),
                    FailureReason = table.Column<string>(type: "TEXT", maxLength: 2000, nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_OptimizationTrials", x => new { x.OptimizationRunId, x.Index });
                    table.ForeignKey(
                        name: "FK_OptimizationTrials_OptimizationRuns_OptimizationRunId",
                        column: x => x.OptimizationRunId,
                        principalTable: "OptimizationRuns",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateIndex(
                name: "IX_BacktestRuns_OptimizationRunId",
                table: "BacktestRuns",
                column: "OptimizationRunId");

            migrationBuilder.CreateIndex(
                name: "IX_OptimizationRuns_CreatedAt",
                table: "OptimizationRuns",
                column: "CreatedAt");

            migrationBuilder.CreateIndex(
                name: "IX_OptimizationRuns_Status",
                table: "OptimizationRuns",
                column: "Status");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "OptimizationTrials");

            migrationBuilder.DropTable(
                name: "OptimizationRuns");

            migrationBuilder.DropIndex(
                name: "IX_BacktestRuns_OptimizationRunId",
                table: "BacktestRuns");

            migrationBuilder.DropColumn(
                name: "OptimizationRunId",
                table: "BacktestRuns");
        }
    }
}
