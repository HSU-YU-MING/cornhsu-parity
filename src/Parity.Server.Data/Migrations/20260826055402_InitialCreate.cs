using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Parity.Server.Data.Migrations;

/// <inheritdoc />
public partial class InitialCreate : Migration
{
    /// <inheritdoc />
    protected override void Up(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.CreateTable(
            name: "Projects",
            columns: table => new
            {
                Id = table.Column<Guid>(type: "TEXT", nullable: false),
                Name = table.Column<string>(type: "TEXT", nullable: false),
                TokenHash = table.Column<string>(type: "TEXT", nullable: false),
                CreatedAt = table.Column<long>(type: "INTEGER", nullable: false)
            },
            constraints: table =>
            {
                table.PrimaryKey("PK_Projects", x => x.Id);
            });

        migrationBuilder.CreateTable(
            name: "Runs",
            columns: table => new
            {
                Id = table.Column<Guid>(type: "TEXT", nullable: false),
                ProjectId = table.Column<Guid>(type: "TEXT", nullable: false),
                CommitSha = table.Column<string>(type: "TEXT", nullable: true),
                Branch = table.Column<string>(type: "TEXT", nullable: true),
                TriggeredBy = table.Column<string>(type: "TEXT", nullable: true),
                CreatedAt = table.Column<long>(type: "INTEGER", nullable: false),
                Score = table.Column<int>(type: "INTEGER", nullable: false),
                GateFailed = table.Column<bool>(type: "INTEGER", nullable: false),
                RawReportJson = table.Column<string>(type: "TEXT", nullable: false)
            },
            constraints: table =>
            {
                table.PrimaryKey("PK_Runs", x => x.Id);
                table.ForeignKey(
                    name: "FK_Runs_Projects_ProjectId",
                    column: x => x.ProjectId,
                    principalTable: "Projects",
                    principalColumn: "Id",
                    onDelete: ReferentialAction.Cascade);
            });

        migrationBuilder.CreateTable(
            name: "PageResults",
            columns: table => new
            {
                Id = table.Column<Guid>(type: "TEXT", nullable: false),
                RunId = table.Column<Guid>(type: "TEXT", nullable: false),
                Route = table.Column<string>(type: "TEXT", nullable: false),
                Url = table.Column<string>(type: "TEXT", nullable: false),
                Score = table.Column<int>(type: "INTEGER", nullable: false),
                DesignNodes = table.Column<int>(type: "INTEGER", nullable: false),
                Matched = table.Column<int>(type: "INTEGER", nullable: false),
                Unmatched = table.Column<int>(type: "INTEGER", nullable: false),
                NodesWithDiffs = table.Column<int>(type: "INTEGER", nullable: false),
                Critical = table.Column<int>(type: "INTEGER", nullable: false),
                Serious = table.Column<int>(type: "INTEGER", nullable: false),
                Medium = table.Column<int>(type: "INTEGER", nullable: false),
                Minor = table.Column<int>(type: "INTEGER", nullable: false),
                MaxSeverity = table.Column<string>(type: "TEXT", nullable: false)
            },
            constraints: table =>
            {
                table.PrimaryKey("PK_PageResults", x => x.Id);
                table.ForeignKey(
                    name: "FK_PageResults_Runs_RunId",
                    column: x => x.RunId,
                    principalTable: "Runs",
                    principalColumn: "Id",
                    onDelete: ReferentialAction.Cascade);
            });

        migrationBuilder.CreateTable(
            name: "Diffs",
            columns: table => new
            {
                Id = table.Column<Guid>(type: "TEXT", nullable: false),
                PageResultId = table.Column<Guid>(type: "TEXT", nullable: false),
                DesignLayer = table.Column<string>(type: "TEXT", nullable: false),
                DesignId = table.Column<string>(type: "TEXT", nullable: false),
                Selector = table.Column<string>(type: "TEXT", nullable: false),
                MatchedBy = table.Column<string>(type: "TEXT", nullable: false),
                Prop = table.Column<string>(type: "TEXT", nullable: false),
                Expected = table.Column<string>(type: "TEXT", nullable: false),
                Actual = table.Column<string>(type: "TEXT", nullable: false),
                Unit = table.Column<string>(type: "TEXT", nullable: true),
                Delta = table.Column<double>(type: "REAL", nullable: true),
                Tolerance = table.Column<double>(type: "REAL", nullable: false),
                Severity = table.Column<string>(type: "TEXT", nullable: false),
                Status = table.Column<string>(type: "TEXT", nullable: false),
                Soft = table.Column<bool>(type: "INTEGER", nullable: false)
            },
            constraints: table =>
            {
                table.PrimaryKey("PK_Diffs", x => x.Id);
                table.ForeignKey(
                    name: "FK_Diffs_PageResults_PageResultId",
                    column: x => x.PageResultId,
                    principalTable: "PageResults",
                    principalColumn: "Id",
                    onDelete: ReferentialAction.Cascade);
            });

        migrationBuilder.CreateIndex(
            name: "IX_Diffs_PageResultId",
            table: "Diffs",
            column: "PageResultId");

        migrationBuilder.CreateIndex(
            name: "IX_PageResults_RunId",
            table: "PageResults",
            column: "RunId");

        migrationBuilder.CreateIndex(
            name: "IX_Projects_TokenHash",
            table: "Projects",
            column: "TokenHash",
            unique: true);

        migrationBuilder.CreateIndex(
            name: "IX_Runs_ProjectId_CreatedAt",
            table: "Runs",
            columns: new[] { "ProjectId", "CreatedAt" });
    }

    /// <inheritdoc />
    protected override void Down(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.DropTable(
            name: "Diffs");

        migrationBuilder.DropTable(
            name: "PageResults");

        migrationBuilder.DropTable(
            name: "Runs");

        migrationBuilder.DropTable(
            name: "Projects");
    }
}
