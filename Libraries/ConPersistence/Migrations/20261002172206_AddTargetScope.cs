using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace ConPersistence.Migrations
{
    /// <inheritdoc />
    public partial class AddTargetScope : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<string>(
                name: "Scope",
                table: "Targets",
                type: "TEXT",
                maxLength: 16,
                nullable: false,
                defaultValue: "Verdict");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "Scope",
                table: "Targets");
        }
    }
}
