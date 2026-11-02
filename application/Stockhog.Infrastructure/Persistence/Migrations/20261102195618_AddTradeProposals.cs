using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Stockhog.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class AddTradeProposals : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<decimal>(
                name: "StopLossPrice",
                table: "Orders",
                type: "TEXT",
                nullable: true);

            migrationBuilder.CreateTable(
                name: "TradeProposals",
                columns: table => new
                {
                    Id = table.Column<string>(type: "TEXT", maxLength: 64, nullable: false),
                    Symbol = table.Column<string>(type: "TEXT", maxLength: 10, nullable: false),
                    StrategyName = table.Column<string>(type: "TEXT", maxLength: 64, nullable: false),
                    Signal = table.Column<string>(type: "TEXT", maxLength: 2000, nullable: false),
                    Quantity = table.Column<decimal>(type: "TEXT", nullable: false),
                    ReferencePrice = table.Column<decimal>(type: "TEXT", nullable: false),
                    StopPrice = table.Column<decimal>(type: "TEXT", nullable: false),
                    RiskAmount = table.Column<decimal>(type: "TEXT", nullable: false),
                    Equity = table.Column<decimal>(type: "TEXT", nullable: false),
                    Score = table.Column<decimal>(type: "TEXT", nullable: true),
                    ScanId = table.Column<string>(type: "TEXT", maxLength: 64, nullable: true),
                    CreatedAt = table.Column<DateTime>(type: "TEXT", nullable: false),
                    ExpiresAt = table.Column<DateTime>(type: "TEXT", nullable: false),
                    Status = table.Column<string>(type: "TEXT", maxLength: 16, nullable: false),
                    DecidedAt = table.Column<DateTime>(type: "TEXT", nullable: true),
                    OrderId = table.Column<string>(type: "TEXT", maxLength: 48, nullable: true),
                    Reason = table.Column<string>(type: "TEXT", maxLength: 2000, nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_TradeProposals", x => x.Id);
                });

            migrationBuilder.CreateIndex(
                name: "IX_TradeProposals_CreatedAt",
                table: "TradeProposals",
                column: "CreatedAt");

            migrationBuilder.CreateIndex(
                name: "IX_TradeProposals_Status",
                table: "TradeProposals",
                column: "Status");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "TradeProposals");

            migrationBuilder.DropColumn(
                name: "StopLossPrice",
                table: "Orders");
        }
    }
}
