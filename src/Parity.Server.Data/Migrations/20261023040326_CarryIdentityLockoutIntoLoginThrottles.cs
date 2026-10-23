using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Parity.Server.Data.Migrations;

/// <summary>
/// 把 Identity 時代的登入失敗狀態(AspNetUsers.AccessFailedCount / LockoutEnd)搬進 LoginThrottles(決策 #21)。
/// 不搬的話,升級前被鎖的帳號一 migrate 就解鎖、錯過幾次的計數歸零。只搬「還有狀態」的帳號:計數 &gt; 0,
/// 或鎖定還沒到期。LockedUntil 存 UTC ticks(與 ServerDbContext 的轉換一致),由 SQLite 的 julianday 換算
/// (精度到幾十微秒)。只寫 SQLite 的 SQL:M5 換供應商時 migrations 會重生(見 ServerDbContext)。
/// Down 不搬回去(Identity 的欄位沒動過,本來就還在)。
/// </summary>
public partial class CarryIdentityLockoutIntoLoginThrottles : Migration
{
    /// <inheritdoc />
    protected override void Up(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.Sql("""
            INSERT OR IGNORE INTO "LoginThrottles" ("NormalizedEmail", "Failures", "LockedUntil")
            SELECT "NormalizedEmail",
                   "AccessFailedCount",
                   CASE WHEN "LockoutEnd" IS NOT NULL AND julianday("LockoutEnd") > julianday('now')
                        THEN CAST(ROUND((julianday("LockoutEnd") - julianday('0001-01-01 00:00:00')) * 864000000000.0) AS INTEGER)
                   END
            FROM "AspNetUsers"
            WHERE "NormalizedEmail" IS NOT NULL
              AND ("AccessFailedCount" > 0 OR ("LockoutEnd" IS NOT NULL AND julianday("LockoutEnd") > julianday('now')));
            """);
    }

    /// <inheritdoc />
    protected override void Down(MigrationBuilder migrationBuilder)
    {
    }
}
