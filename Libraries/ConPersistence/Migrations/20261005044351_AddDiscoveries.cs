using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace ConPersistence.Migrations
{
    /// <inheritdoc />
    public partial class AddDiscoveries : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "Discoveries",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "TEXT", nullable: false),
                    Address = table.Column<string>(type: "TEXT", nullable: false, collation: "NOCASE"),
                    Hostname = table.Column<string>(type: "TEXT", nullable: true),
                    Kind = table.Column<string>(type: "TEXT", maxLength: 24, nullable: false),
                    Method = table.Column<string>(type: "TEXT", maxLength: 24, nullable: false),
                    Origin = table.Column<string>(type: "TEXT", nullable: false),
                    Evidence = table.Column<string>(type: "TEXT", nullable: true),
                    FirstSeenUtc = table.Column<DateTime>(type: "TEXT", nullable: false),
                    LastSeenUtc = table.Column<DateTime>(type: "TEXT", nullable: false),
                    Status = table.Column<string>(type: "TEXT", maxLength: 24, nullable: false),
                    AdoptedTargetId = table.Column<Guid>(type: "TEXT", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_Discoveries", x => x.Id);
                });

            migrationBuilder.CreateIndex(
                name: "IX_Discoveries_Address_Kind",
                table: "Discoveries",
                columns: new[] { "Address", "Kind" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_Discoveries_Status",
                table: "Discoveries",
                column: "Status");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "Discoveries");
        }
    }
}
