using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Parity.Engine;

namespace Parity.Server.Data;

/// <summary>「跟上次比變了什麼」的答案(M4.5 必補 #3)。PrevRunId = null 表示這是該分支第一筆。</summary>
public sealed record RunChanges(
    Guid? PrevRunId,
    DateTimeOffset? PrevCreatedAt,
    IReadOnlyList<ChangedDiff> Regressions,
    IReadOnlyList<ChangedDiff> Worsened,
    IReadOnlyList<ChangedDiff> Fixed,
    int Unchanged);

public sealed record ChangedDiff(
    string Route, string DesignLayer, string Selector, string Prop,
    string Severity, string Expected, string Actual);

/// <summary>
/// 比對一筆 run 與「同專案、同分支的前一筆」——PM 每天真正的問題是「變了什麼」,
/// 不是「現在有什麼」。比對邏輯直接用引擎的 BaselineComparer(與 CI 的 baseline
/// 模式同一套語意:身分 = 路由+圖層+selector+屬性),不另發明一份。
/// </summary>
public static class RunCompare
{
    /// <summary>run 不存在或非成員 → null;是第一筆 → PrevRunId 為 null 的空比對。</summary>
    public static async Task<RunChanges?> ChangesAsync(
        ServerDbContext db, Guid userId, Guid runId, CancellationToken ct = default)
    {
        var visible = Membership.ProjectIdsFor(db, userId);
        var run = await db.Runs
            .Where(r => r.Id == runId && visible.Contains(r.ProjectId))
            .Select(r => new { r.Id, r.ProjectId, r.Branch, r.CreatedAt, r.RawReportGzip })
            .FirstOrDefaultAsync(ct);
        if (run is null) return null;

        var prev = await db.Runs
            .Where(r => r.ProjectId == run.ProjectId && r.Branch == run.Branch
                && r.CreatedAt < run.CreatedAt)
            .OrderByDescending(r => r.CreatedAt)
            .Select(r => new { r.Id, r.CreatedAt, r.RawReportGzip })
            .FirstOrDefaultAsync(ct);
        if (prev is null)
            return new RunChanges(null, null, [], [], [], 0);

        var comparison = BaselineComparer.Compare(
            Records(run.RawReportGzip), Records(prev.RawReportGzip));
        return new RunChanges(
            prev.Id, prev.CreatedAt,
            comparison.Regressions.Select(Map).ToList(),
            comparison.Worsened.Select(Map).ToList(),
            comparison.Fixed.Select(Map).ToList(),
            comparison.Unchanged);
    }

    private static IReadOnlyList<DiffRecord> Records(byte[] blob)
    {
        var doc = JsonSerializer.Deserialize<ReportDocument>(
            ReportBlob.Decompress(blob), ReportWire.Compact)!;
        return DiffRecord.FromReports(doc.Reports);
    }

    private static ChangedDiff Map(DiffRecord d) => new(
        d.Route, d.DesignLayer, d.Selector, d.Prop,
        d.Severity.ToString().ToLowerInvariant(), d.Expected, d.Actual);
}
