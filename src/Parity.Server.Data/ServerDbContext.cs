using Microsoft.EntityFrameworkCore;

namespace Parity.Server.Data;

/// <summary>
/// 網頁外殼的資料庫。從第一天就走正式 EF migrations——Parity.Storage 用 EnsureCreated
/// 起家、後來補 migration 自動接管的那段遷移成本(0.10.0/0.13.1),這裡不再付一次。
/// M1 供應商是 SQLite(開發零依賴);M5 定案換供應商時 migrations 重生(已知成本)。
/// </summary>
public class ServerDbContext(DbContextOptions<ServerDbContext> options) : DbContext(options)
{
    public DbSet<Project> Projects => Set<Project>();
    public DbSet<Run> Runs => Set<Run>();
    public DbSet<PageResult> PageResults => Set<PageResult>();

    protected override void OnModelCreating(ModelBuilder b)
    {
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
