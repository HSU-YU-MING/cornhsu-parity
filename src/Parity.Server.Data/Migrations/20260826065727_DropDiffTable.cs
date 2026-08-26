using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Parity.Server.Data.Migrations;

/// <inheritdoc />
public partial class DropDiffTable : Migration
{
    /// <inheritdoc />
    protected override void Up(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.DropTable(
            name: "Diffs");
    }

    /// <inheritdoc />
    protected override void Down(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.CreateTable(
            name: "Diffs",
            columns: table => new
            {
                Id = table.Column<Guid>(type: "TEXT", nullable: false),
                PageResultId = table.Column<Guid>(type: "TEXT", nullable: false),
                Actual = table.Column<string>(type: "TEXT", nullable: false),
                Delta = table.Column<double>(type: "REAL", nullable: true),
                DesignId = table.Column<string>(type: "TEXT", nullable: false),
                DesignLayer = table.Column<string>(type: "TEXT", nullable: false),
                Expected = table.Column<string>(type: "TEXT", nullable: false),
                MatchedBy = table.Column<string>(type: "TEXT", nullable: false),
                Prop = table.Column<string>(type: "TEXT", nullable: false),
                Selector = table.Column<string>(type: "TEXT", nullable: false),
                Severity = table.Column<string>(type: "TEXT", nullable: false),
                Soft = table.Column<bool>(type: "INTEGER", nullable: false),
                Status = table.Column<string>(type: "TEXT", nullable: false),
                Tolerance = table.Column<double>(type: "REAL", nullable: false),
                Unit = table.Column<string>(type: "TEXT", nullable: true)
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
    }
}
