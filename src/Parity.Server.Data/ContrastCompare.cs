using Microsoft.EntityFrameworkCore;

namespace Parity.Server.Data;

/// <summary>
/// XamlContrast 的「跟上次比變了什麼」。PrevRunId = null 表示這是該分支第一筆 XamlContrast 的 run。
/// PrevToolVersion／PrevExitCode 讓畫面能誠實提醒:兩筆用的工具版本不同時,變化可能來自工具本身;
/// 上一筆被判為不可信(exit 3)時,拿它當基準也不可信。
/// KeyVersion／PrevKeyVersion 是更強的那一種:鍵的寫法本身換了版,整排「新增」與「修好」
/// 都只是同一組配對換了名字(見 <see cref="ContrastCompare.KeyVersionOf"/>)。
/// </summary>
public sealed record ContrastChanges(
    Guid? PrevRunId,
    DateTimeOffset? PrevCreatedAt,
    string? PrevToolVersion,
    int? PrevExitCode,
    int? KeyVersion,
    int? PrevKeyVersion,
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
/// (它的 <c>Baseline.KeyVersion</c> 遞增)時,這裡要一起看:比法本身不必動(鍵的欄位沒變),
/// 但 <see cref="KeyVersionOf"/> 要加那一版的邊界,畫面才說得出「這兩筆的鍵不是同一種」。
/// 報告裡沒有 keyVersion,所以只能照版本號推;toolVersion 不同但鍵同版時,照舊只提醒
/// 「變化可能來自工具」(稽核語意每個 minor 都可能改進)。
/// </summary>
public static class ContrastCompare
{
    public const int MaxLines = 20;
    private const double RatioTolerance = 0.01; // 與 XamlContrast 的 Baseline.Compare 同一個容差

    /// <summary>
    /// 這個工具版本產生的鍵是第幾版(XamlContrast 的 <c>Baseline.KeyVersion</c>)。
    /// 鍵的「寫法」屬稽核語意,1.x 的 minor 就可以改 —— 改了之後同一組配對在新舊兩筆 run 裡
    /// 身分不同,比對會冒出一整排「新增」與「修好」,而那些都不是真的變化。XamlContrast 自己的
    /// <c>--baseline</c> 遇到這種情況是直接拒讀舊檔並叫人重寫;伺服器沒有那個選項(兩筆 run 都已經收了),
    /// 所以至少要說得出「這兩筆的鍵不是同一種」。
    /// 報告裡沒有 keyVersion(XamlContrast 只寫進 baseline 檔),所以這裡照版本號推:
    /// <list type="bullet">
    /// <item>1.6.0 起 = 2 —— element 帶隱含樣式的鏈路徑(<c>⊕Style[implicit …]/base</c>)、
    /// bg 改成模板裡真正畫底的那一層</item>
    /// <item>1.6.0 之前(含 0.x)= 1</item>
    /// <item>認不出來的一律 null,**不猜**:空的、讀不懂的,以及 <c>0.0.0-*</c>(原始碼建置,
    /// 可能是任何一版的程式碼)</item>
    /// </list>
    /// ⚠ XamlContrast 下一次遞增 KeyVersion 時,這裡要加一條邊界 —— 否則儀表板會把鍵的改版
    /// 讀成使用者的程式碼變差了。
    /// </summary>
    public static int? KeyVersionOf(string? toolVersion)
    {
        if (string.IsNullOrWhiteSpace(toolVersion)) return null;
        var core = toolVersion.Split('-', '+')[0];
        if (!Version.TryParse(core, out var v)) return null;
        if (v.Major == 0 && v.Minor == 0) return null; // 0.0.0-dev／0.0.0-ci:看不出是哪一版的程式碼
        return v >= new Version(1, 6, 0) ? 2 : 1;
    }

    /// <summary>run 不存在、非成員、或不是 XamlContrast 的 run → null;是第一筆 → PrevRunId 為 null 的空比對。</summary>
    public static async Task<ContrastChanges?> ChangesAsync(
        ServerDbContext db, Guid userId, Guid runId, CancellationToken ct = default)
    {
        var visible = Membership.ProjectIdsFor(db, userId);
        var run = await db.Runs
            .Where(r => r.Id == runId && visible.Contains(r.ProjectId) && r.Tool == RunTool.XamlContrast)
            .Select(r => new { r.ProjectId, r.Branch, r.CreatedAt, r.RawReportGzip, r.Contrast!.ToolVersion })
            .FirstOrDefaultAsync(ct);
        if (run is null) return null;
        var keyVersion = KeyVersionOf(run.ToolVersion);

        // 「上一筆」只算 XamlContrast 的:同分支夾著的 parity run 不是它的基準
        var prev = await db.Runs
            .Where(r => r.ProjectId == run.ProjectId && r.Branch == run.Branch
                && r.Tool == RunTool.XamlContrast && r.CreatedAt < run.CreatedAt)
            .OrderByDescending(r => r.CreatedAt)
            .Select(r => new { r.Id, r.CreatedAt, r.RawReportGzip, r.Contrast!.ToolVersion, r.Contrast.ExitCode })
            .FirstOrDefaultAsync(ct);
        if (prev is null)
            return new ContrastChanges(null, null, null, null, keyVersion, null, [], [], [], [], 0);

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
            keyVersion, KeyVersionOf(prev.ToolVersion),
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
