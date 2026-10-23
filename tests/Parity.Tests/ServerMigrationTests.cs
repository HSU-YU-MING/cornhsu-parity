using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;
using Parity.Server.Data;

namespace Parity.Tests;

/// <summary>
/// 伺服器 db 的 migration 資料搬移。LoginThrottles(2.0.0)接手登入失敗計數之前,失敗次數與鎖定記在 Identity 的
/// AccessFailedCount / LockoutEnd——驗證者指出 migrate 之後這些狀態就失效了(被鎖的人一升級就解鎖)。
/// </summary>
public class ServerMigrationTests
{
    [Fact]
    public async Task Identity_lockout_state_is_carried_into_login_throttles()
    {
        await using var conn = new SqliteConnection("Data Source=:memory:");
        await conn.OpenAsync();
        await using var db = new ServerDbContext(new DbContextOptionsBuilder<ServerDbContext>().UseSqlite(conn).Options);
        // 停在 LoginThrottles 剛建好、還沒搬資料的那一版
        await db.GetService<IMigrator>().MigrateAsync("20261022124054_AddLoginThrottle");

        var now = DateTimeOffset.UtcNow;
        AppUser User(string email, int failures, DateTimeOffset? lockoutEnd) => new()
        {
            Id = Guid.NewGuid(),
            UserName = email,
            Email = email,
            NormalizedEmail = email.ToUpperInvariant(),
            NormalizedUserName = email.ToUpperInvariant(),
            AccessFailedCount = failures,
            LockoutEnd = lockoutEnd,
            LockoutEnabled = true,
        };
        db.Users.AddRange(
            User("counting@t.local", 2, null),                      // 錯過 2 次、還沒鎖
            User("locked@t.local", 0, now.AddMinutes(4)),           // 鎖定中(Identity 鎖上時把計數歸零)
            User("expired@t.local", 0, now.AddMinutes(-10)),        // 鎖定早就過了:沒有狀態要帶
            User("clean@t.local", 0, null));
        await db.SaveChangesAsync();
        db.ChangeTracker.Clear();

        await db.Database.MigrateAsync();

        var rows = await db.LoginThrottles.AsNoTracking().ToDictionaryAsync(t => t.NormalizedEmail);
        Assert.Equal(["COUNTING@T.LOCAL", "LOCKED@T.LOCAL"], rows.Keys.Order());
        Assert.Equal((2, (DateTimeOffset?)null), (rows["COUNTING@T.LOCAL"].Failures, rows["COUNTING@T.LOCAL"].LockedUntil));
        var locked = rows["LOCKED@T.LOCAL"];
        Assert.Equal(0, locked.Failures);
        Assert.NotNull(locked.LockedUntil);
        Assert.InRange((locked.LockedUntil!.Value - now.AddMinutes(4)).Duration(), TimeSpan.Zero, TimeSpan.FromSeconds(2));
    }
}
