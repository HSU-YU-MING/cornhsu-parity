using System.Text;

namespace Parity.Engine;

/// <summary>還原度分數(0–100):給 PM 看的一個好消化的數字——忠實實作的設計節點比例(有配對且零落差)。</summary>
public static class FidelityScore
{
    public static int Compute(IEnumerable<FidelityReport> reports)
    {
        var list = reports as IReadOnlyList<FidelityReport> ?? reports.ToList();
        var total = list.Sum(r => r.Summary.DesignNodes);
        if (total == 0) return 100;
        var clean = list.Sum(r => r.Nodes.Count(n => n.Diffs.Count == 0));
        return (int)Math.Round(100.0 * clean / total);
    }
}

/// <summary>把一條落差翻成「該怎麼改」——開發者可直接照做的 CSS 目標值(規畫書外的加值:從挑錯變助手)。</summary>
public static class FixHint
{
    public static string? For(PropDiff d) => d.Prop switch
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
        IReadOnlyList<FidelityReport> reports, bool gateFail, BaselineComparison? baseline = null)
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

        // 落差表(附建議修法)
        var rows = reports.SelectMany(r => r.Nodes
            .Where(n => n.Diffs.Count > 0)
            .SelectMany(n => n.Diffs.Select(d => (r.Route, n, d)))).ToList();

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
                var fix = FixHint.For(d) is { } f ? $"`{Esc(f)}`" : "—";
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
