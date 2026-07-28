using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace StudyApp.Web.Migrations
{
    /// <inheritdoc />
    public partial class AddCourseMap : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "CourseMapRevisions",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "TEXT", nullable: false),
                    CourseId = table.Column<Guid>(type: "TEXT", nullable: false),
                    JobId = table.Column<Guid>(type: "TEXT", nullable: true),
                    Status = table.Column<int>(type: "INTEGER", nullable: false),
                    Summary = table.Column<string>(type: "TEXT", nullable: false),
                    AppliedAt = table.Column<DateTime>(type: "TEXT", nullable: true),
                    CreatedAt = table.Column<DateTime>(type: "TEXT", nullable: false),
                    UpdatedAt = table.Column<DateTime>(type: "TEXT", nullable: false),
                    IsDeleted = table.Column<bool>(type: "INTEGER", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_CourseMapRevisions", x => x.Id);
                    table.ForeignKey(
                        name: "FK_CourseMapRevisions_Courses_CourseId",
                        column: x => x.CourseId,
                        principalTable: "Courses",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "CourseTopics",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "TEXT", nullable: false),
                    CourseId = table.Column<Guid>(type: "TEXT", nullable: false),
                    UnitId = table.Column<Guid>(type: "TEXT", nullable: true),
                    Name = table.Column<string>(type: "TEXT", nullable: false),
                    Summary = table.Column<string>(type: "TEXT", nullable: false),
                    Importance = table.Column<int>(type: "INTEGER", nullable: false),
                    ImportanceReason = table.Column<string>(type: "TEXT", nullable: false),
                    ImportancePinned = table.Column<bool>(type: "INTEGER", nullable: false),
                    IsDismissed = table.Column<bool>(type: "INTEGER", nullable: false),
                    LastChangedAt = table.Column<DateTime>(type: "TEXT", nullable: true),
                    PreviousImportance = table.Column<int>(type: "INTEGER", nullable: true),
                    CreatedAt = table.Column<DateTime>(type: "TEXT", nullable: false),
                    UpdatedAt = table.Column<DateTime>(type: "TEXT", nullable: false),
                    IsDeleted = table.Column<bool>(type: "INTEGER", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_CourseTopics", x => x.Id);
                    table.ForeignKey(
                        name: "FK_CourseTopics_CourseUnits_UnitId",
                        column: x => x.UnitId,
                        principalTable: "CourseUnits",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.SetNull);
                    table.ForeignKey(
                        name: "FK_CourseTopics_Courses_CourseId",
                        column: x => x.CourseId,
                        principalTable: "Courses",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "TopicProposals",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "TEXT", nullable: false),
                    RevisionId = table.Column<Guid>(type: "TEXT", nullable: false),
                    CourseTopicId = table.Column<Guid>(type: "TEXT", nullable: true),
                    Kind = table.Column<int>(type: "INTEGER", nullable: false),
                    ProposedName = table.Column<string>(type: "TEXT", nullable: false),
                    ProposedSummary = table.Column<string>(type: "TEXT", nullable: false),
                    ProposedImportance = table.Column<int>(type: "INTEGER", nullable: false),
                    ProposedUnitId = table.Column<Guid>(type: "TEXT", nullable: true),
                    Reason = table.Column<string>(type: "TEXT", nullable: false),
                    MergeIntoTopicId = table.Column<Guid>(type: "TEXT", nullable: true),
                    Status = table.Column<int>(type: "INTEGER", nullable: false),
                    CreatedAt = table.Column<DateTime>(type: "TEXT", nullable: false),
                    UpdatedAt = table.Column<DateTime>(type: "TEXT", nullable: false),
                    IsDeleted = table.Column<bool>(type: "INTEGER", nullable: false),
                    Sources = table.Column<string>(type: "TEXT", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_TopicProposals", x => x.Id);
                    table.ForeignKey(
                        name: "FK_TopicProposals_CourseMapRevisions_RevisionId",
                        column: x => x.RevisionId,
                        principalTable: "CourseMapRevisions",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                    table.ForeignKey(
                        name: "FK_TopicProposals_CourseTopics_CourseTopicId",
                        column: x => x.CourseTopicId,
                        principalTable: "CourseTopics",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.SetNull);
                });

            migrationBuilder.CreateTable(
                name: "TopicSources",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "TEXT", nullable: false),
                    CourseTopicId = table.Column<Guid>(type: "TEXT", nullable: false),
                    MaterialId = table.Column<Guid>(type: "TEXT", nullable: false),
                    SourceReference = table.Column<string>(type: "TEXT", nullable: true),
                    Mention = table.Column<int>(type: "INTEGER", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_TopicSources", x => x.Id);
                    table.ForeignKey(
                        name: "FK_TopicSources_CourseTopics_CourseTopicId",
                        column: x => x.CourseTopicId,
                        principalTable: "CourseTopics",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                    table.ForeignKey(
                        name: "FK_TopicSources_Materials_MaterialId",
                        column: x => x.MaterialId,
                        principalTable: "Materials",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateIndex(
                name: "IX_CourseMapRevisions_CourseId_Status",
                table: "CourseMapRevisions",
                columns: new[] { "CourseId", "Status" });

            migrationBuilder.CreateIndex(
                name: "IX_CourseTopics_CourseId_Importance",
                table: "CourseTopics",
                columns: new[] { "CourseId", "Importance" });

            migrationBuilder.CreateIndex(
                name: "IX_CourseTopics_UnitId",
                table: "CourseTopics",
                column: "UnitId");

            migrationBuilder.CreateIndex(
                name: "IX_TopicProposals_CourseTopicId",
                table: "TopicProposals",
                column: "CourseTopicId");

            migrationBuilder.CreateIndex(
                name: "IX_TopicProposals_RevisionId",
                table: "TopicProposals",
                column: "RevisionId");

            migrationBuilder.CreateIndex(
                name: "IX_TopicSources_CourseTopicId",
                table: "TopicSources",
                column: "CourseTopicId");

            migrationBuilder.CreateIndex(
                name: "IX_TopicSources_MaterialId_Mention",
                table: "TopicSources",
                columns: new[] { "MaterialId", "Mention" });
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "TopicProposals");

            migrationBuilder.DropTable(
                name: "TopicSources");

            migrationBuilder.DropTable(
                name: "CourseMapRevisions");

            migrationBuilder.DropTable(
                name: "CourseTopics");
        }
    }
}
