using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Stockhog.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class AddOrders : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "Orders",
                columns: table => new
                {
                    ClientOrderId = table.Column<string>(type: "TEXT", maxLength: 48, nullable: false),
                    Symbol = table.Column<string>(type: "TEXT", maxLength: 10, nullable: false),
                    Side = table.Column<string>(type: "TEXT", maxLength: 8, nullable: false),
                    Type = table.Column<string>(type: "TEXT", maxLength: 16, nullable: false),
                    Quantity = table.Column<decimal>(type: "TEXT", nullable: false),
                    LimitPrice = table.Column<decimal>(type: "TEXT", nullable: true),
                    StopPrice = table.Column<decimal>(type: "TEXT", nullable: true),
                    TimeInForce = table.Column<string>(type: "TEXT", maxLength: 8, nullable: false),
                    Status = table.Column<string>(type: "TEXT", maxLength: 16, nullable: false),
                    BrokerOrderId = table.Column<string>(type: "TEXT", maxLength: 64, nullable: true),
                    FilledQuantity = table.Column<decimal>(type: "TEXT", nullable: false),
                    AverageFillPrice = table.Column<decimal>(type: "TEXT", nullable: true),
                    RejectReason = table.Column<string>(type: "TEXT", maxLength: 2000, nullable: true),
                    CreatedAt = table.Column<DateTime>(type: "TEXT", nullable: false),
                    SubmittedAt = table.Column<DateTime>(type: "TEXT", nullable: true),
                    UpdatedAt = table.Column<DateTime>(type: "TEXT", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_Orders", x => x.ClientOrderId);
                });

            migrationBuilder.CreateIndex(
                name: "IX_Orders_BrokerOrderId",
                table: "Orders",
                column: "BrokerOrderId");

            migrationBuilder.CreateIndex(
                name: "IX_Orders_CreatedAt",
                table: "Orders",
                column: "CreatedAt");

            migrationBuilder.CreateIndex(
                name: "IX_Orders_Status",
                table: "Orders",
                column: "Status");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "Orders");
        }
    }
}
