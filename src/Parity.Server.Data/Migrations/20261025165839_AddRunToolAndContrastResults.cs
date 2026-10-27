using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Parity.Server.Data.Migrations;

/// <inheritdoc />
public partial class AddRunToolAndContrastResults : Migration
{
    /// <inheritdoc />
    protected override void Up(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.AlterColumn<int>(
            name: "Score",
            table: "Runs",
            type: "INTEGER",
            nullable: true,
            oldClrType: typeof(int),
            oldType: "INTEGER");

        // 手改:產生器給的是 ""。這一版之前伺服器只收 parity,既有的列一律是 parity 的 run
        // (ServerDeploymentTests.Runs_stored_before_the_merged_server_become_parity_runs 釘住)
        migrationBuilder.AddColumn<string>(
            name: "Tool",
            table: "Runs",
            type: "TEXT",
            maxLength: 32,
            nullable: false,
            defaultValue: "parity");

        migrationBuilder.CreateTable(
            name: "ContrastResults",
            columns: table => new
            {
                RunId = table.Column<Guid>(type: "TEXT", nullable: false),
                ToolVersion = table.Column<string>(type: "TEXT", nullable: true),
                ExitCode = table.Column<int>(type: "INTEGER", nullable: true),
                Files = table.Column<int>(type: "INTEGER", nullable: false),
                Pairs = table.Column<int>(type: "INTEGER", nullable: false),
                Coverage = table.Column<double>(type: "REAL", nullable: false),
                Unresolved = table.Column<int>(type: "INTEGER", nullable: false),
                Fail = table.Column<int>(type: "INTEGER", nullable: false),
                Warn = table.Column<int>(type: "INTEGER", nullable: false),
                Ok = table.Column<int>(type: "INTEGER", nullable: false),
                Decorative = table.Column<int>(type: "INTEGER", nullable: false),
                Suppressed = table.Column<int>(type: "INTEGER", nullable: false),
                ParseErrors = table.Column<int>(type: "INTEGER", nullable: false),
                PaletteMode = table.Column<string>(type: "TEXT", nullable: false),
                PaletteDegraded = table.Column<bool>(type: "INTEGER", nullable: false),
                FailOn = table.Column<string>(type: "TEXT", nullable: false)
            },
            constraints: table =>
            {
                table.PrimaryKey("PK_ContrastResults", x => x.RunId);
                table.ForeignKey(
                    name: "FK_ContrastResults_Runs_RunId",
                    column: x => x.RunId,
                    principalTable: "Runs",
                    principalColumn: "Id",
                    onDelete: ReferentialAction.Cascade);
            });
    }

    /// <inheritdoc />
    protected override void Down(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.DropTable(
            name: "ContrastResults");

        migrationBuilder.DropColumn(
            name: "Tool",
            table: "Runs");

        migrationBuilder.AlterColumn<int>(
            name: "Score",
            table: "Runs",
            type: "INTEGER",
            nullable: false,
            defaultValue: 0,
            oldClrType: typeof(int),
            oldType: "INTEGER",
            oldNullable: true);
    }
}
