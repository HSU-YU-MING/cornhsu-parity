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
    }

    /// <summary>把當前落差存成一個新的 baseline 快照,回傳快照 Id。</summary>
    public async Task<int> SaveAsync(
        IReadOnlyList<DiffRecord> diffs, DateTime createdAt,
        string? commit = null, string? branch = null, CancellationToken ct = default)
    {
        var snapshot = new BaselineSnapshot
        {
            CreatedAt = createdAt,
            Commit = commit,
            Branch = branch,
            Diffs = diffs.Select(StoredDiff.From).ToList(),
        };
        _db.Snapshots.Add(snapshot);
        await _db.SaveChangesAsync(ct);
        return snapshot.Id;
    }

    /// <summary>最新一筆 baseline 的落差;沒有任何快照時回 null(代表「還沒建 baseline」)。</summary>
    public async Task<IReadOnlyList<DiffRecord>?> GetLatestAsync(CancellationToken ct = default)
    {
        var latest = await _db.Snapshots
            .OrderByDescending(s => s.CreatedAt).ThenByDescending(s => s.Id)
            .Include(s => s.Diffs)
            .FirstOrDefaultAsync(ct);
        return latest?.Diffs.Select(d => d.ToRecord()).ToList();
    }

    /// <summary>歷史快照(新到舊):時間、commit、落差數——給 `parity baseline list`。</summary>
    public async Task<IReadOnlyList<(int Id, DateTime CreatedAt, string? Commit, int DiffCount)>> HistoryAsync(
        int limit = 20, CancellationToken ct = default)
        => await _db.Snapshots
            .OrderByDescending(s => s.CreatedAt).ThenByDescending(s => s.Id)
            .Take(limit)
            .Select(s => new ValueTuple<int, DateTime, string?, int>(s.Id, s.CreatedAt, s.Commit, s.Diffs.Count))
            .ToListAsync(ct);

    public async ValueTask DisposeAsync() => await _db.DisposeAsync();
}
