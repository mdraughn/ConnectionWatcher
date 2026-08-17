using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace ConPersistence.Migrations
{
    /// <inheritdoc />
    public partial class InitialCreate : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "Profiles",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "TEXT", nullable: false),
                    Name = table.Column<string>(type: "TEXT", nullable: false, collation: "NOCASE"),
                    NormalIntervalSeconds = table.Column<double>(type: "REAL", nullable: false),
                    PingTimeoutSeconds = table.Column<double>(type: "REAL", nullable: false),
                    BurstIntervalSeconds = table.Column<double>(type: "REAL", nullable: false),
                    BurstMaxDurationSeconds = table.Column<double>(type: "REAL", nullable: false),
                    HistorySize = table.Column<int>(type: "INTEGER", nullable: false),
                    MinUsableSample = table.Column<int>(type: "INTEGER", nullable: false),
                    RespondingThreshold = table.Column<double>(type: "REAL", nullable: false),
                    FalteringThreshold = table.Column<double>(type: "REAL", nullable: false),
                    DownThreshold = table.Column<double>(type: "REAL", nullable: false),
                    RecoveringThreshold = table.Column<double>(type: "REAL", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_Profiles", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "Rollups",
                columns: table => new
                {
                    Id = table.Column<long>(type: "INTEGER", nullable: false)
                        .Annotation("Sqlite:Autoincrement", true),
                    TargetId = table.Column<Guid>(type: "TEXT", nullable: false),
                    PeriodStartUtc = table.Column<DateTime>(type: "TEXT", nullable: false),
                    Resolution = table.Column<string>(type: "TEXT", nullable: false),
                    PingCount = table.Column<int>(type: "INTEGER", nullable: false),
                    SuccessCount = table.Column<int>(type: "INTEGER", nullable: false),
                    RttAvg = table.Column<double>(type: "REAL", nullable: false),
                    RttMin = table.Column<long>(type: "INTEGER", nullable: false),
                    RttMax = table.Column<long>(type: "INTEGER", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_Rollups", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "Transitions",
                columns: table => new
                {
                    Id = table.Column<long>(type: "INTEGER", nullable: false)
                        .Annotation("Sqlite:Autoincrement", true),
                    TimestampUtc = table.Column<DateTime>(type: "TEXT", nullable: false),
                    SubjectId = table.Column<Guid>(type: "TEXT", nullable: false),
                    SubjectName = table.Column<string>(type: "TEXT", nullable: false),
                    SubjectKind = table.Column<string>(type: "TEXT", nullable: false),
                    FromState = table.Column<string>(type: "TEXT", nullable: false),
                    ToState = table.Column<string>(type: "TEXT", nullable: false),
                    Trigger = table.Column<string>(type: "TEXT", nullable: false),
                    SuccessRate = table.Column<double>(type: "REAL", nullable: true),
                    SampleSize = table.Column<int>(type: "INTEGER", nullable: true),
                    BurstLossRate = table.Column<double>(type: "REAL", nullable: true),
                    AncestorId = table.Column<Guid>(type: "TEXT", nullable: true),
                    Detail = table.Column<string>(type: "TEXT", nullable: true),
                    Verdict = table.Column<string>(type: "TEXT", nullable: true),
                    AvgRtt = table.Column<float>(type: "REAL", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_Transitions", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "Targets",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "TEXT", nullable: false),
                    Name = table.Column<string>(type: "TEXT", nullable: false, collation: "NOCASE"),
                    Address = table.Column<string>(type: "TEXT", nullable: false),
                    ParentId = table.Column<Guid>(type: "TEXT", nullable: true),
                    ProfileId = table.Column<Guid>(type: "TEXT", nullable: false),
                    Enabled = table.Column<bool>(type: "INTEGER", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_Targets", x => x.Id);
                    table.ForeignKey(
                        name: "FK_Targets_Profiles_ProfileId",
                        column: x => x.ProfileId,
                        principalTable: "Profiles",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                    table.ForeignKey(
                        name: "FK_Targets_Targets_ParentId",
                        column: x => x.ParentId,
                        principalTable: "Targets",
                        principalColumn: "Id");
                });

            migrationBuilder.CreateTable(
                name: "Pools",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "TEXT", nullable: false),
                    Name = table.Column<string>(type: "TEXT", nullable: false, collation: "NOCASE"),
                    ParentId = table.Column<Guid>(type: "TEXT", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_Pools", x => x.Id);
                    table.ForeignKey(
                        name: "FK_Pools_Targets_ParentId",
                        column: x => x.ParentId,
                        principalTable: "Targets",
                        principalColumn: "Id");
                });

            migrationBuilder.CreateTable(
                name: "PoolMembers",
                columns: table => new
                {
                    PoolId = table.Column<Guid>(type: "TEXT", nullable: false),
                    TargetId = table.Column<Guid>(type: "TEXT", nullable: false),
                    MemberIndex = table.Column<int>(type: "INTEGER", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_PoolMembers", x => new { x.PoolId, x.TargetId });
                    table.ForeignKey(
                        name: "FK_PoolMembers_Pools_PoolId",
                        column: x => x.PoolId,
                        principalTable: "Pools",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                    table.ForeignKey(
                        name: "FK_PoolMembers_Targets_TargetId",
                        column: x => x.TargetId,
                        principalTable: "Targets",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateIndex(
                name: "IX_PoolMembers_TargetId",
                table: "PoolMembers",
                column: "TargetId");

            migrationBuilder.CreateIndex(
                name: "IX_Pools_Name",
                table: "Pools",
                column: "Name",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_Pools_ParentId",
                table: "Pools",
                column: "ParentId");

            migrationBuilder.CreateIndex(
                name: "IX_Profiles_Name",
                table: "Profiles",
                column: "Name",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_Rollups_TargetId_Resolution_PeriodStartUtc",
                table: "Rollups",
                columns: new[] { "TargetId", "Resolution", "PeriodStartUtc" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_Targets_Name",
                table: "Targets",
                column: "Name",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_Targets_ParentId",
                table: "Targets",
                column: "ParentId");

            migrationBuilder.CreateIndex(
                name: "IX_Targets_ProfileId",
                table: "Targets",
                column: "ProfileId");

            migrationBuilder.CreateIndex(
                name: "IX_Transitions_SubjectId_TimestampUtc",
                table: "Transitions",
                columns: new[] { "SubjectId", "TimestampUtc" });

            migrationBuilder.CreateIndex(
                name: "IX_Transitions_TimestampUtc",
                table: "Transitions",
                column: "TimestampUtc");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "PoolMembers");

            migrationBuilder.DropTable(
                name: "Rollups");

            migrationBuilder.DropTable(
                name: "Transitions");

            migrationBuilder.DropTable(
                name: "Pools");

            migrationBuilder.DropTable(
                name: "Targets");

            migrationBuilder.DropTable(
                name: "Profiles");
        }
    }
}
