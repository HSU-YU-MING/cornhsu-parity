using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Parity.Server.Data.Migrations;

/// <inheritdoc />
public partial class AddRunRepoUrl : Migration
{
    /// <inheritdoc />
    protected override void Up(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.AddColumn<string>(
            name: "RepoUrl",
            table: "Runs",
            type: "TEXT",
            nullable: true);
    }

    /// <inheritdoc />
    protected override void Down(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.DropColumn(
            name: "RepoUrl",
            table: "Runs");
    }
}
