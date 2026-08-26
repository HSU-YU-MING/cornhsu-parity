using Microsoft.EntityFrameworkCore;

namespace Parity.Server.Data;

/// <summary>總覽卡(M4):每個 專案×route 一張——最新分數、上一次分數(方向)、最後檢查時間。</summary>
public sealed record OverviewCard(
    Guid ProjectId, string Project, string Route, string Url,
    int LastScore, bool LastGateFailed, DateTimeOffset LastAt,
    int? PrevScore, int RunCount);

/// <summary>趨勢點(M4):單一 route 的還原度隨時間。</summary>
public sealed record TrendPoint(
    Guid RunId, DateTimeOffset At, int Score, bool GateFailed, string? CommitSha);

/// <summary>
/// M4 的讀取查詢。抽出來的理由:端點裡的 LINQ 沒辦法單元測試,這裡吃 DbContext
/// 純函式回 DTO,測試用 in-memory SQLite 餵資料就能驗 prev/last 的語意。
/// </summary>
public static class Queries
{
    /// <summary>近 recentRuns 次執行內,每個 專案×route 的最新/上次分數。</summary>
    public static async Task<List<OverviewCard>> OverviewAsync(
        ServerDbContext db, int recentRuns = 500, CancellationToken ct = default)
    {
        // 先取近 N 次 run 的頁面結果(含 run 中繼資料),分組在記憶體做——
        // 個人/小團隊的量級(數百 run)這樣最簡單;真的長大再下推到 SQL。
        var recent = await db.PageResults
            .Where(p => db.Runs.OrderByDescending(r => r.CreatedAt).Take(recentRuns)
                .Select(r => r.Id).Contains(p.RunId))
            .Select(p => new
            {
                p.Route,
                p.Url,
                p.Score,
                RunAt = p.Run!.CreatedAt,
                p.Run.GateFailed,
                p.Run.ProjectId,
                Project = p.Run.Project!.Name,
            })
            .ToListAsync(ct);

        return recent
            .GroupBy(x => (x.ProjectId, x.Route))
            .Select(g =>
            {
                var ordered = g.OrderByDescending(x => x.RunAt).ToList();
                var last = ordered[0];
                return new OverviewCard(
                    g.Key.ProjectId, last.Project, g.Key.Route, last.Url,
                    last.Score, last.GateFailed, last.RunAt,
                    PrevScore: ordered.Count > 1 ? ordered[1].Score : null,
                    RunCount: ordered.Count);
            })
            .OrderBy(c => c.Project).ThenBy(c => c.Route)
            .ToList();
    }

    /// <summary>單一 專案×route 的分數時間序(舊 → 新,最多 limit 點)。</summary>
    public static async Task<List<TrendPoint>> TrendAsync(
        ServerDbContext db, Guid projectId, string route, int limit = 100, CancellationToken ct = default)
    {
        var points = await db.PageResults
            .Where(p => p.Route == route && p.Run!.ProjectId == projectId)
            .OrderByDescending(p => p.Run!.CreatedAt)
            .Take(limit)
            .Select(p => new TrendPoint(
                p.RunId, p.Run!.CreatedAt, p.Score, p.Run.GateFailed, p.Run.CommitSha))
            .ToListAsync(ct);
        points.Reverse(); // 圖要舊 → 新
        return points;
    }
}
