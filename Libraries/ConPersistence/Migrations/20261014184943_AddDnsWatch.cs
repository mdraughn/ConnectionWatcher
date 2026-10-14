using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace ConPersistence.Migrations
{
    /// <inheritdoc />
    public partial class AddDnsWatch : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<bool>(
                name: "SystemManaged",
                table: "Targets",
                type: "INTEGER",
                nullable: false,
                defaultValue: false);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "SystemManaged",
                table: "Targets");
        }
    }
}
