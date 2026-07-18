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
        _db.Database.EnsureCreated();

        // 第一次 schema 演進(0.6.0 加 Score)。EnsureCreated 不會更新既有 db 的結構,
        // 用「加欄位、已存在就略過」的最小遷移——比為單一欄位引入完整 migration 划算。
        try { _db.Database.ExecuteSqlRaw("ALTER TABLE Snapshots ADD COLUMN Score INTEGER"); }
        catch (Microsoft.Data.Sqlite.SqliteException) { /* 欄位已存在(新建或已升級的 db)*/ }
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
