using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Parity.Server.Data.Migrations;

/// <inheritdoc />
/// <summary>RawReportJson(TEXT)→ RawReportGzip(BLOB)。伺服器未發佈,既有 dev 資料庫的原文不遷移(重推一次即可)。</summary>
public partial class CompressRawReport : Migration
{
    /// <inheritdoc />
    protected override void Up(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.DropColumn(
            name: "RawReportJson",
            table: "Runs");

        migrationBuilder.AddColumn<byte[]>(
            name: "RawReportGzip",
            table: "Runs",
            type: "BLOB",
            nullable: false,
            defaultValue: new byte[0]);
    }

    /// <inheritdoc />
    protected override void Down(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.DropColumn(
            name: "RawReportGzip",
            table: "Runs");

        migrationBuilder.AddColumn<string>(
            name: "RawReportJson",
            table: "Runs",
            type: "TEXT",
            nullable: false,
            defaultValue: "");
    }
}
