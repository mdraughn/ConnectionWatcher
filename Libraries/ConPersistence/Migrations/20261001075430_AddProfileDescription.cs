using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace ConPersistence.Migrations
{
    /// <inheritdoc />
    public partial class AddProfileDescription : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<string>(
                name: "Description",
                table: "Profiles",
                type: "TEXT",
                nullable: true);

            // The built-in preset "External" became "Public" (2026-10). Targets
            // reference profiles by id, so renaming the row carries them along.
            // Guarded so a user who already made a custom "Public" keeps both.
            // Name has NOCASE collation, so the comparison ignores case.
            migrationBuilder.Sql(
                "UPDATE Profiles SET Name = 'Public' WHERE Name = 'External' " +
                "AND NOT EXISTS (SELECT 1 FROM Profiles WHERE Name = 'Public');");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "Description",
                table: "Profiles");
        }
    }
}
