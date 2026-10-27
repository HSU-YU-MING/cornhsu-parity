using Microsoft.EntityFrameworkCore;

namespace Parity.Server.Data;

/// <summary>
/// XamlContrast 的「跟上次比變了什麼」。PrevRunId = null 表示這是該分支第一筆 XamlContrast 的 run。
/// PrevToolVersion／PrevExitCode 讓畫面能誠實提醒:兩筆用的工具版本不同時,變化可能來自工具本身;
/// 上一筆被判為不可信(exit 3)時,拿它當基準也不可信。
/// </summary>
public sealed record ContrastChanges(
    Guid? PrevRunId,
    DateTimeOffset? PrevCreatedAt,
    string? PrevToolVersion,
    int? PrevExitCode,
    IReadOnlyList<ContrastChange> Regressions,
    IReadOnlyList<ContrastChange> Worsened,
    IReadOnlyList<ContrastChange> Improved,
    IReadOnlyList<ContrastChange> Fixed,
    int Unchanged);

/// <summary>
/// 一組配對(鍵 = 檔案|元素|前景|底色)在兩次之間的樣子。不帶 Prev 的是這一次、Prev* 是上一次;
/// 不存在的那一側是 null／0。Category 是那組裡最嚴重的(有一處 fail 就是 fail)、Worst 是最差的比值。
/// Lines 是這一次的行號(修好的那組是上一次的),最多 <see cref="ContrastCompare.MaxLines"/> 個。
/// </summary>
public sealed record ContrastChange(
    string File, string Element, string Fg, string Bg,
    string? Category, string? PrevCategory,
    int Count, int PrevCount,
    double? Worst, double? PrevWorst,
    IReadOnlyList<int> Lines);

/// <summary>
/// 比對一筆 XamlContrast 的 run 與「同專案、同分支的前一筆 XamlContrast run」。比法照 XamlContrast 自己的
/// <c>--baseline</c>(那個 repo 的 <c>Baseline.Compare</c>),不另發明一份:
/// <list type="bullet">
/// <item>只看低於 AA 的(fail、warn),不論當時的 gate;ok 與 decorative 不算</item>
/// <item>一組配對的身分是 <c>file|element|fg|bg</c>,**不含行號**——行號隨編輯漂移,含進去每次重排版都是假的「新增」</item>
/// <item>變差 = 同一組的處數變多,或最差比值低了超過 0.01(色盤值被調暗時鍵不會變)</item>
/// <item>修好 = 上一次有、這一次整組不見</item>
/// </list>
/// 比 baseline 多一格「好轉」:處數變少或比值變好、但還低於 AA。baseline 把它算進已知債,
/// 這裡是給人看的變化,修了一部分也該看得到。
/// ⚠ 伺服器沒有引用 XamlContrast 的組件,鍵是照它的寫法手刻的。XamlContrast 改了產生鍵的方式
/// (它的 <c>Baseline.KeyVersion</c> 遞增)時,這裡要一起看;報告裡沒有 keyVersion,
/// 所以畫面在兩筆的 toolVersion 不同時提醒「變化可能來自工具」。
/// </summary>
public static class ContrastCompare
{
    public const int MaxLines = 20;
    private const double RatioTolerance = 0.01; // 與 XamlContrast 的 Baseline.Compare 同一個容差

    /// <summary>run 不存在、非成員、或不是 XamlContrast 的 run → null;是第一筆 → PrevRunId 為 null 的空比對。</summary>
    public static async Task<ContrastChanges?> ChangesAsync(
        ServerDbContext db, Guid userId, Guid runId, CancellationToken ct = default)
    {
        var visible = Membership.ProjectIdsFor(db, userId);
        var run = await db.Runs
            .Where(r => r.Id == runId && visible.Contains(r.ProjectId) && r.Tool == RunTool.XamlContrast)
            .Select(r => new { r.ProjectId, r.Branch, r.CreatedAt, r.RawReportGzip })
            .FirstOrDefaultAsync(ct);
        if (run is null) return null;

        // 「上一筆」只算 XamlContrast 的:同分支夾著的 parity run 不是它的基準
        var prev = await db.Runs
            .Where(r => r.ProjectId == run.ProjectId && r.Branch == run.Branch
                && r.Tool == RunTool.XamlContrast && r.CreatedAt < run.CreatedAt)
            .OrderByDescending(r => r.CreatedAt)
            .Select(r => new { r.Id, r.CreatedAt, r.RawReportGzip, r.Contrast!.ToolVersion, r.Contrast.ExitCode })
            .FirstOrDefaultAsync(ct);
        if (prev is null)
            return new ContrastChanges(null, null, null, null, [], [], [], [], 0);

        var now = Groups(ContrastIngest.Findings(run.RawReportGzip));
        var before = Groups(ContrastIngest.Findings(prev.RawReportGzip));
        List<ContrastChange> regressions = [], worsened = [], improved = [], fixedPairs = [];
        var unchanged = 0;

        foreach (var (key, g) in now)
        {
            if (!before.TryGetValue(key, out var b)) regressions.Add(Change(g, null, g.Lines));
            else if (g.Count > b.Count || Lower(g.Worst, b.Worst)) worsened.Add(Change(g, b, g.Lines));
            else if (g.Count < b.Count || Lower(b.Worst, g.Worst)) improved.Add(Change(g, b, g.Lines));
            else unchanged++;
        }
        foreach (var (key, b) in before)
            if (!now.ContainsKey(key)) fixedPairs.Add(Change(null, b, b.Lines));

        return new ContrastChanges(
            prev.Id, prev.CreatedAt, prev.ToolVersion, prev.ExitCode,
            Ordered(regressions), Ordered(worsened), Ordered(improved), Ordered(fixedPairs), unchanged);
    }

    private sealed record Group(
        string File, string Element, string Fg, string Bg, string Category, int Count, double? Worst, List<int> Lines);

    private static Dictionary<string, Group> Groups(IEnumerable<ContrastIngest.ContrastFinding> findings)
        => findings
            .Where(f => f.Category is "fail" or "warn")
            .GroupBy(f => $"{f.File}|{f.Element}|{f.Fg}|{f.Bg}", StringComparer.Ordinal)
            .ToDictionary(g => g.Key, g =>
            {
                var f = g.First();
                var ratios = g.SelectMany(x => new[] { x.RatioDark, x.RatioLight }).OfType<double>().ToList();
                return new Group(f.File, f.Element, f.Fg, f.Bg,
                    g.Any(x => x.Category == "fail") ? "fail" : "warn",
                    g.Count(),
                    ratios.Count == 0 ? null : ratios.Min(),
                    [.. g.Select(x => x.Line).Order().Take(MaxLines)]);
            }, StringComparer.Ordinal);

    /// <summary>a 比 b 低超過容差;任一邊沒有比值就不比(不猜)。</summary>
    private static bool Lower(double? a, double? b) => a is { } x && b is { } y && x < y - RatioTolerance;

    private static ContrastChange Change(Group? now, Group? before, List<int> lines)
    {
        var any = (now ?? before)!;
        return new ContrastChange(any.File, any.Element, any.Fg, any.Bg,
            now?.Category, before?.Category, now?.Count ?? 0, before?.Count ?? 0, now?.Worst, before?.Worst, lines);
    }

    private static List<ContrastChange> Ordered(List<ContrastChange> list)
        => [.. list.OrderBy(c => c.File, StringComparer.Ordinal).ThenBy(c => c.Lines.Count == 0 ? 0 : c.Lines[0])
            .ThenBy(c => c.Element, StringComparer.Ordinal)];
}
