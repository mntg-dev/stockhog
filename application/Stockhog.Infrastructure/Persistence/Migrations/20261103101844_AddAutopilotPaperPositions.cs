using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Stockhog.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class AddAutopilotPaperPositions : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<string>(
                name: "EntryOrderId",
                table: "AutopilotAssignments",
                type: "TEXT",
                maxLength: 48,
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "PositionMode",
                table: "AutopilotAssignments",
                type: "TEXT",
                maxLength: 8,
                nullable: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "EntryOrderId",
                table: "AutopilotAssignments");

            migrationBuilder.DropColumn(
                name: "PositionMode",
                table: "AutopilotAssignments");
        }
    }
}
