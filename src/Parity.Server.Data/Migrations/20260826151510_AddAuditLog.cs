using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Parity.Server.Data.Migrations;

/// <inheritdoc />
public partial class AddAuditLog : Migration
{
    /// <inheritdoc />
    protected override void Up(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.CreateTable(
            name: "AuditEntry",
            columns: table => new
            {
                Id = table.Column<Guid>(type: "TEXT", nullable: false),
                ProjectId = table.Column<Guid>(type: "TEXT", nullable: false),
                ActorUserId = table.Column<Guid>(type: "TEXT", nullable: true),
                ActorEmail = table.Column<string>(type: "TEXT", nullable: false),
                Action = table.Column<string>(type: "TEXT", nullable: false),
                Detail = table.Column<string>(type: "TEXT", nullable: true),
                At = table.Column<long>(type: "INTEGER", nullable: false)
            },
            constraints: table =>
            {
                table.PrimaryKey("PK_AuditEntry", x => x.Id);
                table.ForeignKey(
                    name: "FK_AuditEntry_Projects_ProjectId",
                    column: x => x.ProjectId,
                    principalTable: "Projects",
                    principalColumn: "Id",
                    onDelete: ReferentialAction.Cascade);
            });

        migrationBuilder.CreateIndex(
            name: "IX_AuditEntry_ProjectId_At",
            table: "AuditEntry",
            columns: new[] { "ProjectId", "At" });
    }

    /// <inheritdoc />
    protected override void Down(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.DropTable(
            name: "AuditEntry");
    }
}
