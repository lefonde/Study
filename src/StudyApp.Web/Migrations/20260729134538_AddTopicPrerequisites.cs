using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace StudyApp.Web.Migrations
{
    /// <inheritdoc />
    public partial class AddTopicPrerequisites : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<string>(
                name: "Prerequisites",
                table: "TopicProposals",
                type: "TEXT",
                nullable: true);

            migrationBuilder.CreateTable(
                name: "TopicPrerequisites",
                columns: table => new
                {
                    CourseTopicId = table.Column<Guid>(type: "TEXT", nullable: false),
                    PrerequisiteTopicId = table.Column<Guid>(type: "TEXT", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_TopicPrerequisites", x => new { x.CourseTopicId, x.PrerequisiteTopicId });
                    table.ForeignKey(
                        name: "FK_TopicPrerequisites_CourseTopics_CourseTopicId",
                        column: x => x.CourseTopicId,
                        principalTable: "CourseTopics",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                    table.ForeignKey(
                        name: "FK_TopicPrerequisites_CourseTopics_PrerequisiteTopicId",
                        column: x => x.PrerequisiteTopicId,
                        principalTable: "CourseTopics",
                        principalColumn: "Id");
                });

            migrationBuilder.CreateIndex(
                name: "IX_TopicPrerequisites_PrerequisiteTopicId",
                table: "TopicPrerequisites",
                column: "PrerequisiteTopicId");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "TopicPrerequisites");

            migrationBuilder.DropColumn(
                name: "Prerequisites",
                table: "TopicProposals");
        }
    }
}
