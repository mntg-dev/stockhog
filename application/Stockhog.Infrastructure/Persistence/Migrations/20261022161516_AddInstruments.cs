using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Stockhog.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class AddInstruments : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "Instruments",
                columns: table => new
                {
                    Symbol = table.Column<string>(type: "TEXT", maxLength: 10, nullable: false),
                    Name = table.Column<string>(type: "TEXT", maxLength: 256, nullable: false),
                    Exchange = table.Column<string>(type: "TEXT", maxLength: 16, nullable: false),
                    RefreshedAt = table.Column<DateTime>(type: "TEXT", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_Instruments", x => x.Symbol);
                });
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "Instruments");
        }
    }
}
