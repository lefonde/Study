using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace StudyApp.Web.Migrations
{
    /// <inheritdoc />
    public partial class AddPracticeReviews : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<bool>(
                name: "IsPractice",
                table: "ReviewLogs",
                type: "INTEGER",
                nullable: false,
                defaultValue: false);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "IsPractice",
                table: "ReviewLogs");
        }
    }
}
