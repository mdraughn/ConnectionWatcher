using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace ConPersistence.Migrations
{
    /// <inheritdoc />
    public partial class AddTargetOperator : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<string>(
                name: "Operator",
                table: "Targets",
                type: "TEXT",
                nullable: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "Operator",
                table: "Targets");
        }
    }
}
