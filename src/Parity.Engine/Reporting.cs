using System.Text;
using Parity.Engine.Model;

namespace Parity.Engine;

/// <summary>
/// 落差排序的「衝擊度」——照人眼在意的順序:嚴重度為主,元素在畫面上的面積為輔
/// (同樣嚴重時,大的、顯眼的先看)。只影響報告呈現順序,不動 gate。
/// </summary>
public static class Impact
{
    public static double Area(NodeResult n) => Math.Max(0, n.RenderedBox.W) * Math.Max(0, n.RenderedBox.H);

    public static IEnumerable<NodeResult> Order(IEnumerable<NodeResult> nodes)
        => nodes.OrderByDescending(n => (int)n.Severity).ThenByDescending(Area);
}

/// <summary>還原度分數(0–100):給 PM 看的一個好消化的數字——忠實實作的設計節點比例(有配對且零落差)。</summary>
public static class FidelityScore
{
    public static int Compute(IEnumerable<FidelityReport> reports)
    {
        var list = reports as IReadOnlyList<FidelityReport> ?? reports.ToList();
        var total = list.Sum(r => r.Summary.DesignNodes);
        if (total == 0) return 100;
        // 「忠實」= 有配對且沒有硬落差;純軟落差(如 font-family stack 差異)不擋 gate,也不扣分,
        // 與 gate 的判定一致。
        var clean = list.Sum(r => r.Nodes.Count(n => !n.Diffs.Any(d => !d.Soft)));
        return (int)Math.Round(100.0 * clean / total);
    }
}

/// <summary>
/// 團隊的 design token(名稱 → 值)。用途:落差建議修法時,若期望值剛好是某個 token,
/// 就提示「用這個 token」而不是生的數字/色碼——貼合設計系統,也是團隊真正想驗的東西。
/// </summary>
public sealed class DesignTokens
{
    private readonly Dictionary<string, string> _colorByHex = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<double, string> _nameBySizePx = new();

    public DesignTokens(IReadOnlyDictionary<string, string> tokens)
    {
        foreach (var (name, value) in tokens)
        {
            if (Rgba.TryParseCss(value, out var c)) _colorByHex.TryAdd(c.ToHex(), name);
            else if (Comparison.Normalizer.ParseCssPx(value) is { } px) _nameBySizePx.TryAdd(px, name);
        }
    }

    // 真正以「px 尺寸」計的屬性。font-weight 是無單位數字,不能跟 px token 共用索引
    // (否則 700px 的 size token 會誤配到 font-weight 700)。
    private static readonly HashSet<string> SizeProps =
    [
        "width", "height", "paddingTop", "paddingRight", "paddingBottom", "paddingLeft",
        "itemSpacing", "cornerRadius", "fontSize", "lineHeight", "letterSpacing",
    ];

    /// <summary>這條落差的「期望值」對應到哪個 token(沒有就 null)。</summary>
    public string? NameFor(PropDiff d)
    {
        if (d.Prop is "color" or "background")
        {
            if (Rgba.TryParseCss(d.Expected, out var c) && _colorByHex.TryGetValue(c.ToHex(), out var n))
                return n;
            return null;
        }
        if (SizeProps.Contains(d.Prop) && double.TryParse(d.Expected, out var px)
            && _nameBySizePx.TryGetValue(px, out var sn))
            return sn;
        return null;
    }

    /// <summary>從 JSON(平面的 {"name":"value"} 物件)載入;檔案不存在或解析失敗回 null。</summary>
    public static DesignTokens? LoadJson(string path)
    {
        try
        {
            if (!File.Exists(path)) return null;
            var map = System.Text.Json.JsonSerializer.Deserialize<Dictionary<string, string>>(File.ReadAllText(path));
            return map is { Count: > 0 } ? new DesignTokens(map) : null;
        }
        catch { return null; }
    }
}

/// <summary>把一條落差翻成「該怎麼改」——開發者可直接照做的 CSS 目標值(規畫書外的加值:從挑錯變助手)。</summary>
public static class FixHint
{
    /// <summary>tokens 有給且期望值命中某 token 時,附註「用哪個 token」。</summary>
    public static string? For(PropDiff d, DesignTokens? tokens = null)
    {
        var css = Css(d);
        if (css is null) return null;
        var token = tokens?.NameFor(d);
        return token is null ? css : $"{css}(token: {token})";
    }

    private static string? Css(PropDiff d) => d.Prop switch
    {
        "width" => $"width: {d.Expected}px",
        "height" => $"height: {d.Expected}px",
        "paddingTop" => $"padding-top: {d.Expected}px",
        "paddingRight" => $"padding-right: {d.Expected}px",
        "paddingBottom" => $"padding-bottom: {d.Expected}px",
        "paddingLeft" => $"padding-left: {d.Expected}px",
        "itemSpacing" => $"gap: {d.Expected}px",
        "cornerRadius" => $"border-radius: {d.Expected}px",
        "fontSize" => $"font-size: {d.Expected}px",
        "fontWeight" => $"font-weight: {d.Expected}",
        "lineHeight" => $"line-height: {d.Expected}px",
        "letterSpacing" => $"letter-spacing: {d.Expected}px",
        "color" => $"color: {d.Expected}",
        "background" => $"background: {d.Expected}",
        "fontFamily" => $"font-family 應含 {d.Expected}",
        _ => null,
    };
}

/// <summary>
/// 把報告輸出成 Markdown——可讀、可分享、可貼 PR 留言(規畫書「給 PM 讀的報告」那半的載體)。
/// 含:還原度分數、gate 結論、落差表(附建議修法)、相對 baseline 的變化、未配對清單。
/// </summary>
public static class MarkdownReport
{
    public static string Render(
        IReadOnlyList<FidelityReport> reports, bool gateFail,
        BaselineComparison? baseline = null, DesignTokens? tokens = null)
    {
        var score = FidelityScore.Compute(reports);
        var total = reports.Sum(r => r.Summary.DesignNodes);
        var clean = reports.Sum(r => r.Nodes.Count(n => n.Diffs.Count == 0));
        var multi = reports.Count > 1;

        var sb = new StringBuilder();
        sb.AppendLine("## Parity — 設計還原度報告");
        sb.AppendLine();
        sb.AppendLine($"**還原度 {score}/100** · {(gateFail ? "❌ **GATE FAIL**" : "✅ PASS")} · " +
            $"{clean}/{total} 個設計節點忠實實作");
        sb.AppendLine();

        // baseline 模式:先講「相對基準變了什麼」——PM/reviewer 最在意的
        if (baseline is not null)
        {
            sb.AppendLine($"相對基準:🔴 新增 **{baseline.Regressions.Count}**、" +
                $"🟠 惡化 **{baseline.Worsened.Count}**、🟢 修好 **{baseline.Fixed.Count}**、⚪ 不變 {baseline.Unchanged}");
            sb.AppendLine();
            AppendChangeList(sb, "🔴 新增落差", baseline.Regressions);
            AppendChangeList(sb, "🟠 惡化", baseline.Worsened);
            if (baseline.Fixed.Count > 0)
                AppendChangeList(sb, "🟢 已修好", baseline.Fixed);
        }

        // 落差表(附建議修法)——依衝擊度排序:嚴重度為主、畫面面積為輔,重要的先看
        var rows = reports
            .SelectMany(r => r.Nodes.Where(n => n.Diffs.Count > 0).Select(n => (r.Route, Node: n)))
            .OrderByDescending(x => (int)x.Node.Severity).ThenByDescending(x => Impact.Area(x.Node))
            .SelectMany(x => x.Node.Diffs.Select(d => (x.Route, x.Node, d)))
            .ToList();

        if (rows.Count > 0)
        {
            sb.AppendLine("### 落差與建議修法");
            sb.AppendLine();
            sb.AppendLine(multi ? "| 路由 | 元素 | 屬性 | 期望 → 實際 | 嚴重度 | 建議 |"
                                : "| 元素 | 屬性 | 期望 → 實際 | 嚴重度 | 建議 |");
            sb.AppendLine(multi ? "|---|---|---|---|---|---|" : "|---|---|---|---|---|");
            foreach (var (route, n, d) in rows)
            {
                var arrow = $"`{Esc(d.Expected)}` → `{Esc(d.Actual)}`" +
                    (d.Delta is { } dl ? $" (Δ{dl})" : "");
                var sev = d.Soft ? $"{d.Severity.ToString().ToLowerInvariant()}(soft)" : d.Severity.ToString().ToLowerInvariant();
                var fix = FixHint.For(d, tokens) is { } f ? $"`{Esc(f)}`" : "—";
                var cells = multi
                    ? $"| {Esc(route)} | `{Esc(n.DesignLayer)}` | {d.Prop} | {arrow} | {sev} | {fix} |"
                    : $"| `{Esc(n.DesignLayer)}` | {d.Prop} | {arrow} | {sev} | {fix} |";
                sb.AppendLine(cells);
            }
            sb.AppendLine();
        }

        // 未配對
        var unmatched = reports.SelectMany(r => r.Unmatched.Select(u => (r.Route, u))).ToList();
        if (unmatched.Count > 0)
        {
            sb.AppendLine($"### 未配對({unmatched.Count})— 需 `data-parity` 或 `parity map` 補");
            sb.AppendLine();
            foreach (var (route, u) in unmatched)
                sb.AppendLine($"- {(multi ? $"`{Esc(route)}` " : "")}`{Esc(u.DesignLayer)}` — {u.Reason}");
            sb.AppendLine();
        }

        sb.AppendLine("<sub>由 Parity 產生 · 數值級設計還原度檢查</sub>");
        return sb.ToString();
    }

    private static void AppendChangeList(StringBuilder sb, string title, IReadOnlyList<DiffRecord> items)
    {
        if (items.Count == 0) return;
        sb.AppendLine($"**{title}**");
        foreach (var d in items)
            sb.AppendLine($"- `{Esc(d.DesignLayer)}` · {d.Prop} · `{Esc(d.Expected)}` → `{Esc(d.Actual)}`");
        sb.AppendLine();
    }

    /// <summary>Markdown 表格用:跳脫會破壞表格/格式的字元。</summary>
    private static string Esc(string s) => s.Replace("|", "\\|").Replace("\n", " ");
}
