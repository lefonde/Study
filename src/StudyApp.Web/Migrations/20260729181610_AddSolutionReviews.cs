using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace StudyApp.Web.Migrations
{
    /// <inheritdoc />
    public partial class AddSolutionReviews : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<Guid>(
                name: "SubmissionForId",
                table: "Materials",
                type: "TEXT",
                nullable: true);

            migrationBuilder.CreateTable(
                name: "SubmissionReviews",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "TEXT", nullable: false),
                    MaterialId = table.Column<Guid>(type: "TEXT", nullable: false),
                    JobId = table.Column<Guid>(type: "TEXT", nullable: true),
                    Summary = table.Column<string>(type: "TEXT", nullable: false),
                    CreatedAt = table.Column<DateTime>(type: "TEXT", nullable: false),
                    UpdatedAt = table.Column<DateTime>(type: "TEXT", nullable: false),
                    IsDeleted = table.Column<bool>(type: "INTEGER", nullable: false),
                    Findings = table.Column<string>(type: "TEXT", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_SubmissionReviews", x => x.Id);
                    table.ForeignKey(
                        name: "FK_SubmissionReviews_Materials_MaterialId",
                        column: x => x.MaterialId,
                        principalTable: "Materials",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateIndex(
                name: "IX_Materials_SubmissionForId",
                table: "Materials",
                column: "SubmissionForId");

            migrationBuilder.CreateIndex(
                name: "IX_SubmissionReviews_MaterialId",
                table: "SubmissionReviews",
                column: "MaterialId");

            migrationBuilder.AddForeignKey(
                name: "FK_Materials_Materials_SubmissionForId",
                table: "Materials",
                column: "SubmissionForId",
                principalTable: "Materials",
                principalColumn: "Id",
                onDelete: ReferentialAction.SetNull);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropForeignKey(
                name: "FK_Materials_Materials_SubmissionForId",
                table: "Materials");

            migrationBuilder.DropTable(
                name: "SubmissionReviews");

            migrationBuilder.DropIndex(
                name: "IX_Materials_SubmissionForId",
                table: "Materials");

            migrationBuilder.DropColumn(
                name: "SubmissionForId",
                table: "Materials");
        }
    }
}
