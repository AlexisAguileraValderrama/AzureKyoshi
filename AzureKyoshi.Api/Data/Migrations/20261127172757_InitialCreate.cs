using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace AzureKyoshi.Api.Data.Migrations
{
    /// <inheritdoc />
    public partial class InitialCreate : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "StudyFolders",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    OwnerUserId = table.Column<string>(type: "nvarchar(128)", maxLength: 128, nullable: false),
                    Name = table.Column<string>(type: "nvarchar(80)", maxLength: 80, nullable: false),
                    ColorHex = table.Column<string>(type: "nvarchar(20)", maxLength: 20, nullable: false),
                    CertificationCode = table.Column<string>(type: "nvarchar(20)", maxLength: 20, nullable: true),
                    CreatedAtUtc = table.Column<DateTime>(type: "datetime2", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_StudyFolders", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "StudyModules",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    OwnerUserId = table.Column<string>(type: "nvarchar(128)", maxLength: 128, nullable: false),
                    FolderId = table.Column<Guid>(type: "uniqueidentifier", nullable: true),
                    Title = table.Column<string>(type: "nvarchar(250)", maxLength: 250, nullable: false),
                    RequestedTopic = table.Column<string>(type: "nvarchar(250)", maxLength: 250, nullable: false),
                    CertificationCode = table.Column<string>(type: "nvarchar(20)", maxLength: 20, nullable: true),
                    SummaryMarkdown = table.Column<string>(type: "nvarchar(max)", nullable: false),
                    SourceContent = table.Column<string>(type: "nvarchar(max)", nullable: false),
                    SourcesJson = table.Column<string>(type: "nvarchar(max)", nullable: false),
                    GeneratedWithFoundryAi = table.Column<bool>(type: "bit", nullable: false),
                    RetrievedViaMcp = table.Column<bool>(type: "bit", nullable: false),
                    CreatedAtUtc = table.Column<DateTime>(type: "datetime2", nullable: false),
                    UpdatedAtUtc = table.Column<DateTime>(type: "datetime2", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_StudyModules", x => x.Id);
                    table.ForeignKey(
                        name: "FK_StudyModules_StudyFolders_FolderId",
                        column: x => x.FolderId,
                        principalTable: "StudyFolders",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.SetNull);
                });

            migrationBuilder.CreateTable(
                name: "QaMessages",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    StudyModuleId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    Role = table.Column<string>(type: "nvarchar(20)", maxLength: 20, nullable: false),
                    Content = table.Column<string>(type: "nvarchar(max)", nullable: false),
                    CreatedAtUtc = table.Column<DateTime>(type: "datetime2", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_QaMessages", x => x.Id);
                    table.ForeignKey(
                        name: "FK_QaMessages_StudyModules_StudyModuleId",
                        column: x => x.StudyModuleId,
                        principalTable: "StudyModules",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "StudyQuizzes",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    StudyModuleId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    Title = table.Column<string>(type: "nvarchar(200)", maxLength: 200, nullable: false),
                    Difficulty = table.Column<string>(type: "nvarchar(30)", maxLength: 30, nullable: false),
                    CreatedAtUtc = table.Column<DateTime>(type: "datetime2", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_StudyQuizzes", x => x.Id);
                    table.ForeignKey(
                        name: "FK_StudyQuizzes_StudyModules_StudyModuleId",
                        column: x => x.StudyModuleId,
                        principalTable: "StudyModules",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "QuizAttempts",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    StudyQuizId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    UserId = table.Column<string>(type: "nvarchar(128)", maxLength: 128, nullable: false),
                    Score = table.Column<int>(type: "int", nullable: false),
                    TotalQuestions = table.Column<int>(type: "int", nullable: false),
                    SelectedOptionIndicesJson = table.Column<string>(type: "nvarchar(1000)", maxLength: 1000, nullable: false),
                    CompletedAtUtc = table.Column<DateTime>(type: "datetime2", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_QuizAttempts", x => x.Id);
                    table.ForeignKey(
                        name: "FK_QuizAttempts_StudyQuizzes_StudyQuizId",
                        column: x => x.StudyQuizId,
                        principalTable: "StudyQuizzes",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "QuizQuestions",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    StudyQuizId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    OrderIndex = table.Column<int>(type: "int", nullable: false),
                    QuestionText = table.Column<string>(type: "nvarchar(max)", nullable: false),
                    OptionsJson = table.Column<string>(type: "nvarchar(max)", nullable: false),
                    CorrectOptionIndex = table.Column<int>(type: "int", nullable: false),
                    Explanation = table.Column<string>(type: "nvarchar(max)", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_QuizQuestions", x => x.Id);
                    table.ForeignKey(
                        name: "FK_QuizQuestions_StudyQuizzes_StudyQuizId",
                        column: x => x.StudyQuizId,
                        principalTable: "StudyQuizzes",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateIndex(
                name: "IX_QaMessages_StudyModuleId_CreatedAtUtc",
                table: "QaMessages",
                columns: new[] { "StudyModuleId", "CreatedAtUtc" });

            migrationBuilder.CreateIndex(
                name: "IX_QuizAttempts_StudyQuizId",
                table: "QuizAttempts",
                column: "StudyQuizId");

            migrationBuilder.CreateIndex(
                name: "IX_QuizQuestions_StudyQuizId",
                table: "QuizQuestions",
                column: "StudyQuizId");

            migrationBuilder.CreateIndex(
                name: "IX_StudyFolders_OwnerUserId_Name",
                table: "StudyFolders",
                columns: new[] { "OwnerUserId", "Name" });

            migrationBuilder.CreateIndex(
                name: "IX_StudyModules_FolderId",
                table: "StudyModules",
                column: "FolderId");

            migrationBuilder.CreateIndex(
                name: "IX_StudyModules_OwnerUserId_CreatedAtUtc",
                table: "StudyModules",
                columns: new[] { "OwnerUserId", "CreatedAtUtc" });

            migrationBuilder.CreateIndex(
                name: "IX_StudyQuizzes_StudyModuleId",
                table: "StudyQuizzes",
                column: "StudyModuleId");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "QaMessages");

            migrationBuilder.DropTable(
                name: "QuizAttempts");

            migrationBuilder.DropTable(
                name: "QuizQuestions");

            migrationBuilder.DropTable(
                name: "StudyQuizzes");

            migrationBuilder.DropTable(
                name: "StudyModules");

            migrationBuilder.DropTable(
                name: "StudyFolders");
        }
    }
}
