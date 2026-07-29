using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace StudyApp.Web.Migrations
{
    /// <inheritdoc />
    public partial class AddCardTopicCoverage : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<string>(
                name: "TargetTopicIds",
                table: "GenerationJobs",
                type: "TEXT",
                nullable: false,
                defaultValue: "[]");

            migrationBuilder.AddColumn<Guid>(
                name: "CourseTopicId",
                table: "CardSuggestions",
                type: "TEXT",
                nullable: true);

            migrationBuilder.CreateTable(
                name: "CardTopics",
                columns: table => new
                {
                    CardId = table.Column<Guid>(type: "TEXT", nullable: false),
                    CourseTopicId = table.Column<Guid>(type: "TEXT", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_CardTopics", x => new { x.CardId, x.CourseTopicId });
                    table.ForeignKey(
                        name: "FK_CardTopics_Cards_CardId",
                        column: x => x.CardId,
                        principalTable: "Cards",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                    table.ForeignKey(
                        name: "FK_CardTopics_CourseTopics_CourseTopicId",
                        column: x => x.CourseTopicId,
                        principalTable: "CourseTopics",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateIndex(
                name: "IX_CardSuggestions_CourseTopicId",
                table: "CardSuggestions",
                column: "CourseTopicId");

            migrationBuilder.CreateIndex(
                name: "IX_CardTopics_CourseTopicId",
                table: "CardTopics",
                column: "CourseTopicId");

            migrationBuilder.AddForeignKey(
                name: "FK_CardSuggestions_CourseTopics_CourseTopicId",
                table: "CardSuggestions",
                column: "CourseTopicId",
                principalTable: "CourseTopics",
                principalColumn: "Id",
                onDelete: ReferentialAction.SetNull);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropForeignKey(
                name: "FK_CardSuggestions_CourseTopics_CourseTopicId",
                table: "CardSuggestions");

            migrationBuilder.DropTable(
                name: "CardTopics");

            migrationBuilder.DropIndex(
                name: "IX_CardSuggestions_CourseTopicId",
                table: "CardSuggestions");

            migrationBuilder.DropColumn(
                name: "TargetTopicIds",
                table: "GenerationJobs");

            migrationBuilder.DropColumn(
                name: "CourseTopicId",
                table: "CardSuggestions");
        }
    }
}
