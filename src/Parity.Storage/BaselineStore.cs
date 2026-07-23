using Microsoft.EntityFrameworkCore;
using Parity.Engine;

namespace Parity.Storage;

/// <summary>
/// baseline 的存取門面(規畫書 M5)。開一個本機 SQLite 檔,提供:
///   - 存快照(把當前落差當作「已接受的基準」)
///   - 取最新 baseline 的落差 → 交給 BaselineComparer 比對「相對基準有沒有變差」
///   - 列出歷史快照
/// </summary>
public sealed class BaselineStore : IAsyncDisposable
{
    private readonly BaselineDbContext _db;

    public BaselineStore(string dbPath)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(dbPath))!);
        // Pooling=False:短命 CLI 不需要連線池;dispose 後即時釋放檔案 handle
        // (否則池化連線會鎖著 baseline.db,serve 重掃或測試刪檔會撞鎖)
        var options = new DbContextOptionsBuilder<BaselineDbContext>()
            .UseSqlite($"Data Source={dbPath};Pooling=False")
            .Options;
        _db = new BaselineDbContext(options);
        AdoptLegacyThenMigrate(_db);
    }

    /// <summary>
    /// schema 演進走 EF migrations。但 0.9.x 之前的 db 是 EnsureCreated 建的:有資料表、
    /// 卻沒有 __EFMigrationsHistory。這種 db 直接 Migrate 會因 InitialCreate 要 CREATE TABLE
    /// 撞上既存表而爆。對策:先把現有 migration 標記為「已套用」(schema 老早就在了),
    /// 再 Migrate——新 db 照常建、舊 db(含已 commit 進使用者 repo 的)無痛接管。
    /// </summary>
    private static void AdoptLegacyThenMigrate(BaselineDbContext db)
    {
        var conn = db.Database.GetDbConnection();
        conn.Open();
        try
        {
            bool TableExists(string name)
            {
                using var cmd = conn.CreateCommand();
                cmd.CommandText = "SELECT 1 FROM sqlite_master WHERE type='table' AND name=$n";
                var p = cmd.CreateParameter();
                p.ParameterName = "$n";
                p.Value = name;
                cmd.Parameters.Add(p);
                using var reader = cmd.ExecuteReader();
                return reader.Read();
            }

            if (TableExists("Snapshots") && !TableExists("__EFMigrationsHistory"))
            {
                db.Database.ExecuteSqlRaw(
                    """CREATE TABLE "__EFMigrationsHistory" ("MigrationId" TEXT NOT NULL CONSTRAINT "PK___EFMigrationsHistory" PRIMARY KEY, "ProductVersion" TEXT NOT NULL);""");
                // 只標記「基準」migration(InitialCreate)——legacy db 的 schema 恰好等於它。
                // **不能標記全部**:未來新增第 2 個 migration 時,legacy db 並沒有它的 schema 變更,
                // 若把它也標成已套用,Migrate 會跳過 → 欄位沒建 → 壞。標基準、其餘交給下面 Migrate() 補。
                var baseline = db.Database.GetMigrations().FirstOrDefault();
                if (baseline is not null)
                    db.Database.ExecuteSqlRaw(
                        """INSERT INTO "__EFMigrationsHistory" ("MigrationId", "ProductVersion") VALUES ({0}, {1});""",
                        baseline, "10.0.0");
            }
        }
        finally
        {
            conn.Close();
        }

        db.Database.Migrate();
    }

    /// <summary>把當前落差存成一個新的 baseline 快照,回傳快照 Id。</summary>
    public async Task<int> SaveAsync(
        IReadOnlyList<DiffRecord> diffs, DateTime createdAt,
        string? commit = null, string? branch = null, int? score = null, CancellationToken ct = default)
    {
        var snapshot = new BaselineSnapshot
        {
            CreatedAt = createdAt,
            Commit = commit,
            Branch = branch,
            Score = score,
            Diffs = diffs.Select(StoredDiff.From).ToList(),
        };
        _db.Snapshots.Add(snapshot);
        await _db.SaveChangesAsync(ct);
        return snapshot.Id;
    }

    /// <summary>最新一筆 baseline(落差 + 存檔當時的分數);沒有任何快照時回 null(代表「還沒建 baseline」)。</summary>
    public async Task<LatestBaseline?> GetLatestAsync(CancellationToken ct = default)
    {
        var latest = await _db.Snapshots
            .OrderByDescending(s => s.CreatedAt).ThenByDescending(s => s.Id)
            .Include(s => s.Diffs)
            .FirstOrDefaultAsync(ct);
        return latest is null
            ? null
            : new LatestBaseline(latest.Diffs.Select(d => d.ToRecord()).ToList(), latest.Score);
    }

    /// <summary>歷史快照(新到舊):時間、commit、落差數、分數——給 `parity baseline list`(分數欄 = 走勢)。</summary>
    public async Task<IReadOnlyList<(int Id, DateTime CreatedAt, string? Commit, int DiffCount, int? Score)>> HistoryAsync(
        int limit = 20, CancellationToken ct = default)
        => await _db.Snapshots
            .OrderByDescending(s => s.CreatedAt).ThenByDescending(s => s.Id)
            .Take(limit)
            .Select(s => new ValueTuple<int, DateTime, string?, int, int?>(s.Id, s.CreatedAt, s.Commit, s.Diffs.Count, s.Score))
            .ToListAsync(ct);

    public async ValueTask DisposeAsync() => await _db.DisposeAsync();
}

/// <summary>最新 baseline 的內容:落差集合 + 存檔當時的還原度分數(舊快照沒存分數 → null)。</summary>
public sealed record LatestBaseline(IReadOnlyList<DiffRecord> Diffs, int? Score);
