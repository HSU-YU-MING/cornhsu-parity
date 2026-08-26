using Microsoft.AspNetCore.Identity.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore;

namespace Parity.Server.Data;

/// <summary>
/// 網頁外殼的資料庫。從第一天就走正式 EF migrations——Parity.Storage 用 EnsureCreated
/// 起家、後來補 migration 自動接管的那段遷移成本(0.10.0/0.13.1),這裡不再付一次。
/// M1 供應商是 SQLite(開發零依賴);M5 定案換供應商時 migrations 重生(已知成本)。
/// M3 起繼承 IdentityUserContext(只有使用者表,沒有 AspNetRoles——角色是專案級成員資格)。
/// </summary>
public class ServerDbContext(DbContextOptions<ServerDbContext> options)
    : IdentityUserContext<AppUser, Guid>(options)
{
    public DbSet<Project> Projects => Set<Project>();
    public DbSet<Run> Runs => Set<Run>();
    public DbSet<PageResult> PageResults => Set<PageResult>();
    public DbSet<ProjectMember> Members => Set<ProjectMember>();
    public DbSet<Invite> Invites => Set<Invite>();

    protected override void OnModelCreating(ModelBuilder b)
    {
        base.OnModelCreating(b); // Identity 的表配置

        b.Entity<ProjectMember>().HasIndex(m => new { m.ProjectId, m.UserId }).IsUnique();
        b.Entity<ProjectMember>().HasOne(m => m.Project).WithMany()
            .HasForeignKey(m => m.ProjectId).OnDelete(DeleteBehavior.Cascade);
        b.Entity<ProjectMember>().HasOne(m => m.User).WithMany()
            .HasForeignKey(m => m.UserId).OnDelete(DeleteBehavior.Cascade);
        b.Entity<ProjectMember>().Property(m => m.CreatedAt).HasConversion(
            v => v.UtcTicks, v => new DateTimeOffset(v, TimeSpan.Zero));

        b.Entity<Invite>().HasIndex(i => i.TokenHash).IsUnique();
        b.Entity<Invite>().HasOne(i => i.Project).WithMany()
            .HasForeignKey(i => i.ProjectId).OnDelete(DeleteBehavior.Cascade);
        // 可空欄位(AcceptedAt)要用「非可空 → long」的正式 ValueConverter,EF 才會自己
        // 包 null 語意——用可空 lambda 硬轉會讓 `AcceptedAt == null` 譯不進 SQL(實踩 500)
        var ticks = new Microsoft.EntityFrameworkCore.Storage.ValueConversion
            .ValueConverter<DateTimeOffset, long>(
                v => v.UtcTicks, v => new DateTimeOffset(v, TimeSpan.Zero));
        b.Entity<Invite>().Property(i => i.CreatedAt).HasConversion(ticks);
        b.Entity<Invite>().Property(i => i.ExpiresAt).HasConversion(ticks);
        b.Entity<Invite>().Property(i => i.AcceptedAt).HasConversion(ticks);

        b.Entity<AuditEntry>().HasIndex(a => new { a.ProjectId, a.At });
        b.Entity<AuditEntry>().HasOne(a => a.Project).WithMany()
            .HasForeignKey(a => a.ProjectId).OnDelete(DeleteBehavior.Cascade);
        b.Entity<AuditEntry>().Property(a => a.At).HasConversion(ticks);

        // SQLite 不支援 DateTimeOffset 進 ORDER BY——存 UTC ticks(INTEGER):
        // 排序嚴格按時間(不像內建 ToBinary 轉換在不同時區偏移下排不準),換供應商也通用。
        b.Entity<Project>().Property(p => p.CreatedAt).HasConversion(
            v => v.UtcTicks, v => new DateTimeOffset(v, TimeSpan.Zero));
        b.Entity<Run>().Property(r => r.CreatedAt).HasConversion(
            v => v.UtcTicks, v => new DateTimeOffset(v, TimeSpan.Zero));

        b.Entity<Project>().HasIndex(p => p.TokenHash).IsUnique(); // token 驗證走索引查找
        b.Entity<Run>().HasIndex(r => new { r.ProjectId, r.CreatedAt });
        b.Entity<PageResult>().HasIndex(p => p.RunId);

        b.Entity<Project>().HasMany(p => p.Runs).WithOne(r => r.Project!)
            .HasForeignKey(r => r.ProjectId).OnDelete(DeleteBehavior.Cascade);
        b.Entity<Run>().HasMany(r => r.Pages).WithOne(p => p.Run!)
            .HasForeignKey(p => p.RunId).OnDelete(DeleteBehavior.Cascade);
    }
}
