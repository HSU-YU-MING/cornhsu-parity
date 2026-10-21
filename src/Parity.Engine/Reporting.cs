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

/// <summary>
/// 團隊的 design token(名稱 → 值)。用途:落差建議修法時,若期望值剛好是某個 token,
/// 就提示「用這個 token」而不是生的數字/色碼——貼合設計系統,也是團隊真正想驗的東西。
/// </summary>
public sealed class DesignTokens
{
    private readonly Dictionary<string, string> _colorByHex = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<double, string> _nameBySizePx = new();
    private readonly List<(string Name, Rgba Color)> _colors = [];
    private readonly List<(string Name, double Px)> _sizes = [];

    public DesignTokens(IReadOnlyDictionary<string, string> tokens)
    {
        foreach (var (name, value) in tokens)
        {
            if (Rgba.TryParseCss(value, out var c))
            {
                _colorByHex.TryAdd(c.ToHex(), name);
                _colors.Add((name, c));
            }
            else if (Comparison.Normalizer.ParseCssPx(value) is { } px)
            {
                _nameBySizePx.TryAdd(px, name);
                _sizes.Add((name, px));
            }
        }
    }

    /// <summary>所有顏色 token(名稱 + 值)。design lint 的允許集合。</summary>
    public IReadOnlyList<(string Name, Rgba Color)> Colors => _colors;

    /// <summary>所有尺寸 token(名稱 + px)。design lint 的允許集合(間距/字級/圓角共用同一份 scale)。</summary>
    public IReadOnlyList<(string Name, double Px)> Sizes => _sizes;

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

    /// <summary>檔案裡一個 token 都沒有(<c>{}</c>)。</summary>
    public bool IsEmpty { get; private init; }

    private static readonly System.Text.Json.JsonDocumentOptions TokenFileOptions = new()
    {
        // 與 parity.config.json、parity.map.json 同一套寬鬆規則——三個檔並排放,只有一個吃註解是暗雷
        CommentHandling = System.Text.Json.JsonCommentHandling.Skip,
        AllowTrailingCommas = true,
    };

    /// <summary>
    /// 從 JSON(平面的 {"name":"value"} 物件)載入。問題一律講清楚是哪個檔、哪裡:檔案不存在 →
    /// FileNotFoundException;不是合法 JSON(附行號)、不是物件、值不是字串 → InvalidOperationException。
    /// (原本的 LoadJson 用 catch 把這些全吞成「沒有 token」,lint 接著叫人去設一個已經設了的 tokensFile。)
    /// 要不要因此擋下指令由呼叫端決定:lint 必需 token,check --md / report 只是少了提示。
    /// </summary>
    public static DesignTokens Load(string path)
    {
        if (!File.Exists(path))
            throw new FileNotFoundException($"tokensFile not found: {path}", path);

        System.Text.Json.JsonDocument doc;
        try
        {
            doc = System.Text.Json.JsonDocument.Parse(File.ReadAllText(path), TokenFileOptions);
        }
        catch (System.Text.Json.JsonException ex)
        {
            throw new InvalidOperationException($"tokensFile {path} is not valid JSON: {ex.Message}", ex);
        }

        using (doc)
        {
            if (doc.RootElement.ValueKind != System.Text.Json.JsonValueKind.Object)
                throw new InvalidOperationException(
                    $"tokensFile {path} must be a flat JSON object ({{\"token name\": \"value\"}}), not {Kind(doc.RootElement.ValueKind)}.");
            var map = new Dictionary<string, string>();
            foreach (var p in doc.RootElement.EnumerateObject())
            {
                if (p.Value.ValueKind != System.Text.Json.JsonValueKind.String)
                    throw new InvalidOperationException(
                        $"tokensFile {path}: \"{p.Name}\" is {Kind(p.Value.ValueKind)}, but token values must be strings " +
                        "(e.g. \"16px\", \"#2563EB\").");
                map[p.Name] = p.Value.GetString()!;
            }
            return new DesignTokens(map) { IsEmpty = map.Count == 0 };
        }

        static string Kind(System.Text.Json.JsonValueKind k) => k switch
        {
            System.Text.Json.JsonValueKind.Number => "a number",
            System.Text.Json.JsonValueKind.Array => "an array",
            System.Text.Json.JsonValueKind.Object => "an object",
            System.Text.Json.JsonValueKind.True or System.Text.Json.JsonValueKind.False => "a boolean",
            System.Text.Json.JsonValueKind.Null => "null",
            _ => k.ToString().ToLowerInvariant(),
        };
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
        "fontFamily" => $"font-family should include {d.Expected}",
        // 相對位置:沒有單一 CSS 可以給(取決於版面做法),給人話方向提示
        "offsetX" => $"horizontal offset from its sibling/parent should be {d.Expected}px",
        "offsetY" => $"vertical offset from its sibling/parent should be {d.Expected}px",
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
        BaselineComparison? baseline = null, DesignTokens? tokens = null,
        IReadOnlyList<string>? gateNotes = null,
        string? figmaFileKey = null, int? baselineScore = null)
    {
        var score = FidelityScore.Compute(reports);
        var total = reports.Sum(r => r.Summary.DesignNodes);
        var clean = reports.Sum(r => r.Nodes.Count(FidelityScore.IsFaithful));
        var multi = reports.Count > 1;

        // 圖層名連回 Figma 節點(設計來源是 Figma 才有;設計師點了直接跳到那個圖層)
        string Layer(string name, string designId)
            => figmaFileKey is not null && designId.Length > 0
                ? $"[`{Esc(name)}`](https://www.figma.com/design/{Uri.EscapeDataString(figmaFileKey)}?node-id={Uri.EscapeDataString(designId.Replace(':', '-'))})"
                : $"`{Esc(name)}`";

        // 分數走勢(baseline 模式且基準有存分數):PM 一眼看到方向
        var trend = baselineScore is { } bs
            ? $" (baseline {bs} {(score > bs ? $"↑ +{score - bs}" : score < bs ? $"↓ {score - bs}" : "→ ±0")})"
            : "";

        var sb = new StringBuilder();
        sb.AppendLine("## Parity — design fidelity report");
        sb.AppendLine();
        sb.AppendLine($"**Fidelity {score}/100**{trend} · {(gateFail ? "❌ **GATE FAIL**" : "✅ PASS")} · " +
            $"{clean}/{total} design nodes implemented faithfully");
        sb.AppendLine();

        // 配對可信度警示(如 0 配對):放最前面——這種情況下面的表是空的,分數也不可信
        if (gateNotes is { Count: > 0 })
        {
            foreach (var note in gateNotes)
                sb.AppendLine($"> ⚠️ {Esc(note)}");
            sb.AppendLine();
        }

        // baseline 模式:先講「相對基準變了什麼」——PM/reviewer 最在意的
        if (baseline is not null)
        {
            sb.AppendLine($"vs baseline: 🔴 new **{baseline.Regressions.Count}**, " +
                $"🟠 worsened **{baseline.Worsened.Count}**, 🟢 fixed **{baseline.Fixed.Count}**, ⚪ unchanged {baseline.Unchanged}");
            sb.AppendLine();
            AppendChangeList(sb, "🔴 New diffs", baseline.Regressions);
            AppendChangeList(sb, "🟠 Worsened", baseline.Worsened);
            if (baseline.Fixed.Count > 0)
                AppendChangeList(sb, "🟢 Fixed", baseline.Fixed);
        }

        // 落差表(附建議修法)——依衝擊度排序:嚴重度為主、畫面面積為輔,重要的先看
        var rows = reports
            .SelectMany(r => r.Nodes.Where(n => n.Diffs.Count > 0).Select(n => (r.Route, Node: n)))
            .OrderByDescending(x => (int)x.Node.Severity).ThenByDescending(x => Impact.Area(x.Node))
            .SelectMany(x => x.Node.Diffs.Select(d => (x.Route, x.Node, d)))
            .ToList();

        if (rows.Count > 0)
        {
            sb.AppendLine("### Diffs and suggested fixes");
            sb.AppendLine();
            sb.AppendLine(multi ? "| Route | Element | Property | Expected → Actual | Severity | Suggested fix |"
                                : "| Element | Property | Expected → Actual | Severity | Suggested fix |");
            sb.AppendLine(multi ? "|---|---|---|---|---|---|" : "|---|---|---|---|---|");
            foreach (var (route, n, d) in rows)
            {
                var arrow = $"`{Esc(d.Expected)}` → `{Esc(d.Actual)}`" +
                    (d.Delta is { } dl ? $" (Δ{dl})" : "");
                var sev = d.Soft ? $"{d.Severity.ToString().ToLowerInvariant()}(soft)" : d.Severity.ToString().ToLowerInvariant();
                var fix = FixHint.For(d, tokens) is { } f ? $"`{Esc(f)}`" : "—";
                var cells = multi
                    ? $"| {Esc(route)} | {Layer(n.DesignLayer, n.DesignId)} | {d.Prop} | {arrow} | {sev} | {fix} |"
                    : $"| {Layer(n.DesignLayer, n.DesignId)} | {d.Prop} | {arrow} | {sev} | {fix} |";
                sb.AppendLine(cells);
            }
            sb.AppendLine();
        }

        // 未配對
        var unmatched = reports.SelectMany(r => r.Unmatched.Select(u => (r.Route, u))).ToList();
        if (unmatched.Count > 0)
        {
            sb.AppendLine($"### Unmatched ({unmatched.Count}) — resolve with `data-parity` or `parity map`");
            sb.AppendLine();
            foreach (var (route, u) in unmatched)
                sb.AppendLine($"- {(multi ? $"`{Esc(route)}` " : "")}{Layer(u.DesignLayer, u.DesignId)} — {u.Reason}");
            sb.AppendLine();
        }

        sb.AppendLine("<sub>Generated by Parity · property-level design fidelity checking</sub>");
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
