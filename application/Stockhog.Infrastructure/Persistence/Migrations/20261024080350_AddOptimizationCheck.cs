using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Stockhog.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class AddOptimizationCheck : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<string>(
                name: "CheckFailureReason",
                table: "OptimizationTrials",
                type: "TEXT",
                maxLength: 2000,
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "CheckMetrics",
                table: "OptimizationTrials",
                type: "TEXT",
                maxLength: 1000,
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "CheckRunId",
                table: "OptimizationTrials",
                type: "TEXT",
                maxLength: 128,
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "CheckStatus",
                table: "OptimizationTrials",
                type: "TEXT",
                maxLength: 16,
                nullable: true);

            migrationBuilder.AddColumn<DateOnly>(
                name: "CheckEnd",
                table: "OptimizationRuns",
                type: "TEXT",
                nullable: true);

            migrationBuilder.AddColumn<DateOnly>(
                name: "CheckStart",
                table: "OptimizationRuns",
                type: "TEXT",
                nullable: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "CheckFailureReason",
                table: "OptimizationTrials");

            migrationBuilder.DropColumn(
                name: "CheckMetrics",
                table: "OptimizationTrials");

            migrationBuilder.DropColumn(
                name: "CheckRunId",
                table: "OptimizationTrials");

            migrationBuilder.DropColumn(
                name: "CheckStatus",
                table: "OptimizationTrials");

            migrationBuilder.DropColumn(
                name: "CheckEnd",
                table: "OptimizationRuns");

            migrationBuilder.DropColumn(
                name: "CheckStart",
                table: "OptimizationRuns");
        }
    }
}
