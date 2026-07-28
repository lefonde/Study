using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace StudyApp.Web.Migrations
{
    /// <inheritdoc />
    public partial class AddDeckUnitAndProgressSnapshots : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<Guid>(
                name: "UnitId",
                table: "Decks",
                type: "TEXT",
                nullable: true);

            migrationBuilder.CreateTable(
                name: "ProgressSnapshots",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "TEXT", nullable: false),
                    DeckId = table.Column<Guid>(type: "TEXT", nullable: false),
                    CourseId = table.Column<Guid>(type: "TEXT", nullable: false),
                    CapturedOn = table.Column<DateOnly>(type: "TEXT", nullable: false),
                    Total = table.Column<int>(type: "INTEGER", nullable: false),
                    Unseen = table.Column<int>(type: "INTEGER", nullable: false),
                    Learning = table.Column<int>(type: "INTEGER", nullable: false),
                    Young = table.Column<int>(type: "INTEGER", nullable: false),
                    Mature = table.Column<int>(type: "INTEGER", nullable: false),
                    Mastery = table.Column<double>(type: "REAL", nullable: false),
                    RecallNow = table.Column<double>(type: "REAL", nullable: false),
                    Retention = table.Column<double>(type: "REAL", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_ProgressSnapshots", x => x.Id);
                    table.ForeignKey(
                        name: "FK_ProgressSnapshots_Courses_CourseId",
                        column: x => x.CourseId,
                        principalTable: "Courses",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                    table.ForeignKey(
                        name: "FK_ProgressSnapshots_Decks_DeckId",
                        column: x => x.DeckId,
                        principalTable: "Decks",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateIndex(
                name: "IX_Decks_UnitId",
                table: "Decks",
                column: "UnitId");

            migrationBuilder.CreateIndex(
                name: "IX_ProgressSnapshots_CourseId_CapturedOn",
                table: "ProgressSnapshots",
                columns: new[] { "CourseId", "CapturedOn" });

            migrationBuilder.CreateIndex(
                name: "IX_ProgressSnapshots_DeckId_CapturedOn",
                table: "ProgressSnapshots",
                columns: new[] { "DeckId", "CapturedOn" },
                unique: true);

            migrationBuilder.AddForeignKey(
                name: "FK_Decks_CourseUnits_UnitId",
                table: "Decks",
                column: "UnitId",
                principalTable: "CourseUnits",
                principalColumn: "Id",
                onDelete: ReferentialAction.SetNull);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropForeignKey(
                name: "FK_Decks_CourseUnits_UnitId",
                table: "Decks");

            migrationBuilder.DropTable(
                name: "ProgressSnapshots");

            migrationBuilder.DropIndex(
                name: "IX_Decks_UnitId",
                table: "Decks");

            migrationBuilder.DropColumn(
                name: "UnitId",
                table: "Decks");
        }
    }
}
