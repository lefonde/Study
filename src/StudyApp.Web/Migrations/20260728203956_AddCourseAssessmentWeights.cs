using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace StudyApp.Web.Migrations
{
    /// <inheritdoc />
    public partial class AddCourseAssessmentWeights : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<string>(
                name: "AssessmentWeights",
                table: "Courses",
                type: "TEXT",
                nullable: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "AssessmentWeights",
                table: "Courses");
        }
    }
}
