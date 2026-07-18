using Microsoft.EntityFrameworkCore;
using Parity.Engine;

namespace Parity.Storage;

/// <summary>一次被接受的 baseline 快照:某個時間點(可含 git commit)當時的落差集合。</summary>
public sealed class BaselineSnapshot
{
    public int Id { get; set; }
    public DateTime CreatedAt { get; set; }
    public string? Commit { get; set; }
    public string? Branch { get; set; }
    /// <summary>存檔當下的還原度分數(0–100)。快照序列 = 分數走勢,給 PM 看方向。0.6.0 加入,舊快照為 null。</summary>
    public int? Score { get; set; }
    public List<StoredDiff> Diffs { get; set; } = [];
}

/// <summary>快照裡的一條落差(對應 DiffRecord;Severity 存字串,報表好讀)。</summary>
public sealed class StoredDiff
{
    public int Id { get; set; }
    public int SnapshotId { get; set; }
    public string Route { get; set; } = "";
    public string DesignLayer { get; set; } = "";
    public string Selector { get; set; } = "";
    public string Prop { get; set; } = "";
    public Severity Severity { get; set; }
    public string Expected { get; set; } = "";
    public string Actual { get; set; } = "";

    public DiffRecord ToRecord() => new(Route, DesignLayer, Selector, Prop, Severity, Expected, Actual);

    public static StoredDiff From(DiffRecord d) => new()
    {
        Route = d.Route, DesignLayer = d.DesignLayer, Selector = d.Selector,
        Prop = d.Prop, Severity = d.Severity, Expected = d.Expected, Actual = d.Actual,
    };
}

/// <summary>baseline 的本機 SQLite 儲存(規畫書 M5)。單一檔案 db,結構簡單 → 用 EnsureCreated,不走 migration。</summary>
public sealed class BaselineDbContext(DbContextOptions<BaselineDbContext> options) : DbContext(options)
{
    public DbSet<BaselineSnapshot> Snapshots => Set<BaselineSnapshot>();
    public DbSet<StoredDiff> Diffs => Set<StoredDiff>();

    protected override void OnModelCreating(ModelBuilder b)
    {
        b.Entity<BaselineSnapshot>().HasMany(s => s.Diffs).WithOne().HasForeignKey(d => d.SnapshotId)
            .OnDelete(DeleteBehavior.Cascade);
        b.Entity<StoredDiff>().Property(d => d.Severity).HasConversion<string>();
        b.Entity<StoredDiff>().HasIndex(d => d.SnapshotId);
    }
}
