using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace StudyApp.Web.Migrations
{
    /// <inheritdoc />
    public partial class AddMaterialsAndStructure : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<Guid>(
                name: "CurrentUnitId",
                table: "Courses",
                type: "TEXT",
                nullable: true);

            migrationBuilder.AddColumn<Guid>(
                name: "SourceMaterialId",
                table: "Cards",
                type: "TEXT",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "SourceReference",
                table: "Cards",
                type: "TEXT",
                nullable: true);

            migrationBuilder.AddColumn<Guid>(
                name: "UnitId",
                table: "Cards",
                type: "TEXT",
                nullable: true);

            migrationBuilder.CreateTable(
                name: "CourseUnits",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "TEXT", nullable: false),
                    CourseId = table.Column<Guid>(type: "TEXT", nullable: false),
                    ParentId = table.Column<Guid>(type: "TEXT", nullable: true),
                    Kind = table.Column<int>(type: "INTEGER", nullable: false),
                    Title = table.Column<string>(type: "TEXT", nullable: false),
                    Order = table.Column<int>(type: "INTEGER", nullable: false),
                    CreatedAt = table.Column<DateTime>(type: "TEXT", nullable: false),
                    UpdatedAt = table.Column<DateTime>(type: "TEXT", nullable: false),
                    IsDeleted = table.Column<bool>(type: "INTEGER", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_CourseUnits", x => x.Id);
                    table.ForeignKey(
                        name: "FK_CourseUnits_CourseUnits_ParentId",
                        column: x => x.ParentId,
                        principalTable: "CourseUnits",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_CourseUnits_Courses_CourseId",
                        column: x => x.CourseId,
                        principalTable: "Courses",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "Materials",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "TEXT", nullable: false),
                    CourseId = table.Column<Guid>(type: "TEXT", nullable: false),
                    UnitId = table.Column<Guid>(type: "TEXT", nullable: true),
                    Kind = table.Column<int>(type: "INTEGER", nullable: false),
                    Title = table.Column<string>(type: "TEXT", nullable: false),
                    FilePath = table.Column<string>(type: "TEXT", nullable: false),
                    MimeType = table.Column<string>(type: "TEXT", nullable: false),
                    SizeBytes = table.Column<long>(type: "INTEGER", nullable: false),
                    DueDate = table.Column<DateTime>(type: "TEXT", nullable: true),
                    Status = table.Column<int>(type: "INTEGER", nullable: false),
                    CreatedAt = table.Column<DateTime>(type: "TEXT", nullable: false),
                    UpdatedAt = table.Column<DateTime>(type: "TEXT", nullable: false),
                    IsDeleted = table.Column<bool>(type: "INTEGER", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_Materials", x => x.Id);
                    table.ForeignKey(
                        name: "FK_Materials_CourseUnits_UnitId",
                        column: x => x.UnitId,
                        principalTable: "CourseUnits",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.SetNull);
                    table.ForeignKey(
                        name: "FK_Materials_Courses_CourseId",
                        column: x => x.CourseId,
                        principalTable: "Courses",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateIndex(
                name: "IX_Courses_CurrentUnitId",
                table: "Courses",
                column: "CurrentUnitId");

            migrationBuilder.CreateIndex(
                name: "IX_Cards_SourceMaterialId",
                table: "Cards",
                column: "SourceMaterialId");

            migrationBuilder.CreateIndex(
                name: "IX_Cards_UnitId",
                table: "Cards",
                column: "UnitId");

            migrationBuilder.CreateIndex(
                name: "IX_CourseUnits_CourseId_ParentId_Order",
                table: "CourseUnits",
                columns: new[] { "CourseId", "ParentId", "Order" });

            migrationBuilder.CreateIndex(
                name: "IX_CourseUnits_ParentId",
                table: "CourseUnits",
                column: "ParentId");

            migrationBuilder.CreateIndex(
                name: "IX_Materials_CourseId_Kind",
                table: "Materials",
                columns: new[] { "CourseId", "Kind" });

            migrationBuilder.CreateIndex(
                name: "IX_Materials_DueDate",
                table: "Materials",
                column: "DueDate");

            migrationBuilder.CreateIndex(
                name: "IX_Materials_UnitId",
                table: "Materials",
                column: "UnitId");

            migrationBuilder.AddForeignKey(
                name: "FK_Cards_CourseUnits_UnitId",
                table: "Cards",
                column: "UnitId",
                principalTable: "CourseUnits",
                principalColumn: "Id",
                onDelete: ReferentialAction.SetNull);

            migrationBuilder.AddForeignKey(
                name: "FK_Cards_Materials_SourceMaterialId",
                table: "Cards",
                column: "SourceMaterialId",
                principalTable: "Materials",
                principalColumn: "Id",
                onDelete: ReferentialAction.SetNull);

            migrationBuilder.AddForeignKey(
                name: "FK_Courses_CourseUnits_CurrentUnitId",
                table: "Courses",
                column: "CurrentUnitId",
                principalTable: "CourseUnits",
                principalColumn: "Id",
                onDelete: ReferentialAction.SetNull);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropForeignKey(
                name: "FK_Cards_CourseUnits_UnitId",
                table: "Cards");

            migrationBuilder.DropForeignKey(
                name: "FK_Cards_Materials_SourceMaterialId",
                table: "Cards");

            migrationBuilder.DropForeignKey(
                name: "FK_Courses_CourseUnits_CurrentUnitId",
                table: "Courses");

            migrationBuilder.DropTable(
                name: "Materials");

            migrationBuilder.DropTable(
                name: "CourseUnits");

            migrationBuilder.DropIndex(
                name: "IX_Courses_CurrentUnitId",
                table: "Courses");

            migrationBuilder.DropIndex(
                name: "IX_Cards_SourceMaterialId",
                table: "Cards");

            migrationBuilder.DropIndex(
                name: "IX_Cards_UnitId",
                table: "Cards");

            migrationBuilder.DropColumn(
                name: "CurrentUnitId",
                table: "Courses");

            migrationBuilder.DropColumn(
                name: "SourceMaterialId",
                table: "Cards");

            migrationBuilder.DropColumn(
                name: "SourceReference",
                table: "Cards");

            migrationBuilder.DropColumn(
                name: "UnitId",
                table: "Cards");
        }
    }
}
