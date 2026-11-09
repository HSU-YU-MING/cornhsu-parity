using System.Text;
using System.Text.RegularExpressions;
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
        "text" => $"text should read: {d.Expected}",
        "fontStyle" => $"font-style: {d.Expected}",
        "textDecoration" => $"text-decoration-line: {d.Expected}",
        "textTransform" => d.Expected == "small-caps" ? "font-variant-caps: small-caps" : $"text-transform: {d.Expected}",
        "textAlign" => $"text-align: {d.Expected}",
        "lineCount" => $"this text should take {d.Expected} line(s)",
        "opacity" => $"opacity: {d.Expected}",
        "borderWidth" => $"border-width: {d.Expected} (px, top right bottom left when they differ)",
        "borderColor" => $"border-color: {d.Expected}",
        "outlineWidth" => d.Expected == "0" ? "outline: none" : $"outline-width: {d.Expected}px",
        "outlineColor" => $"outline-color: {d.Expected}",
        "shadow" => $"box-shadow / text-shadow: {d.Expected}",
        "shadowColor" => $"shadow color: {d.Expected}",
        "gradient" => $"background-image: {d.Expected}",
        "image" => "use the same image as the reference",
        "childOrder" => $"children should be in this order: {d.Expected}",
        "mainAxisOffset" => $"justify-content: the first child should be {d.Expected}px from the container's start",
        "crossAxisOffset" => $"align-items / align-self: this child should be {d.Expected}px from the container's edge",
        "relation" => $"this text should sit {d.Expected}",
        // 相對位置:沒有單一 CSS 可以給(取決於版面做法),給人話方向提示
        "offsetX" => $"horizontal offset from its sibling/parent should be {d.Expected}px",
        "offsetY" => $"vertical offset from its sibling/parent should be {d.Expected}px",
        _ => null,
    };
}

/// <summary>
/// Markdown 報告最上面的警示。內容直接當 Markdown 輸出:工具自己寫的句子可以直接放,來自資料的值(route、圖層名)
/// 要先用 <see cref="MarkdownReport.Name"/> 或 <see cref="MarkdownReport.Word"/> 包好。
/// 刻意不收 string:主控台的訊息長得一模一樣,直接拿來用,圖層名就能在 PR 留言裡寫任意 Markdown(2.3.0 就是這樣漏的)。
/// </summary>
public sealed record MarkdownNote(string Markdown);

/// <summary>
/// 把報告輸出成 Markdown——可讀、可分享、可貼 PR 留言(規畫書「給 PM 讀的報告」那半的載體)。
/// 含:還原度分數、gate 結論、落差表(附建議修法)、相對 baseline 的變化、未配對清單。
/// </summary>
public static class MarkdownReport
{
    public static string Render(
        IReadOnlyList<FidelityReport> reports, bool gateFail,
        BaselineComparison? baseline = null, DesignTokens? tokens = null,
        IReadOnlyList<MarkdownNote>? gateNotes = null,
        string? figmaFileKey = null, int? baselineScore = null)
    {
        var score = FidelityScore.Compute(reports);
        var total = reports.Sum(r => r.Summary.DesignNodes);
        var clean = reports.Sum(r => r.Nodes.Count(FidelityScore.IsFaithful));
        var multi = reports.Count > 1;

        // 圖層名連回 Figma 節點(設計來源是 Figma 才有;設計師點了直接跳到那個圖層)
        string Layer(string name, string designId)
            => figmaFileKey is not null && designId.Length > 0
                ? $"[{Name(name)}](https://www.figma.com/design/{Uri.EscapeDataString(figmaFileKey)}?node-id={Uri.EscapeDataString(designId.Replace(':', '-'))})"
                : Name(name);

        // 分數走勢(baseline 模式且基準有存分數):PM 一眼看到方向
        var trend = baselineScore is { } bs
            ? $" (baseline {bs} {(score > bs ? $"↑ +{score - bs}" : score < bs ? $"↓ {score - bs}" : "→ ±0")})"
            : "";

        var sb = new StringBuilder();
        sb.AppendLine("## Parity — design fidelity report");
        sb.AppendLine();
        // 未配對的節點沒有比到任何屬性,分數看起來高也可能只是沒比到,所以放在最前面。清單在下面
        var unmatchedCount = reports.Sum(r => r.Summary.Unmatched);
        sb.AppendLine($"**Fidelity {score}/100**{trend} · {(gateFail ? "❌ **GATE FAIL**" : "✅ PASS")} · " +
            $"{clean}/{total} design nodes implemented faithfully" +
            (unmatchedCount > 0 ? $" · {unmatchedCount} unmatched, not compared" : ""));
        sb.AppendLine();

        // 配對可信度警示(如 0 配對):放最前面——這種情況下面的表是空的,分數也不可信
        if (gateNotes is { Count: > 0 })
        {
            foreach (var note in gateNotes)
                sb.AppendLine($"> ⚠️ {note.Markdown}");
            sb.AppendLine();
        }

        // 字型沒載到(2.5.0):文字是用系統字型量的,下面那些寬度、換行、位置的落差可能全是這一個原因。
        // 字型名來自頁面的 CSS,一律過 Name()
        foreach (var r in reports.Where(r => r.FontsNotLoaded is { Count: > 0 }))
        {
            sb.AppendLine($"> ⚠️ {(multi ? $"{Word(r.Route)}: " : "")}web font(s) did not load: " +
                $"{string.Join(", ", r.FontsNotLoaded!.Select(Name))}. Text was measured with a fallback font, " +
                "so text widths, line breaks and positions may differ for that reason alone.");
            sb.AppendLine();
        }

        // baseline 模式:先講「相對基準變了什麼」——PM/reviewer 最在意的
        if (baseline is not null)
        {
            sb.AppendLine($"vs baseline: 🔴 new **{baseline.Regressions.Count}**, " +
                $"🟠 worsened **{baseline.Worsened.Count}**, 🟢 fixed **{baseline.Fixed.Count}**, ⚪ unchanged {baseline.Unchanged}" +
                (baseline.Ignored > 0 ? $", ignored {baseline.Ignored} (soft diffs a 1.x baseline saved — re-save the baseline to drop them)" : ""));
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
                // Code:期望與實際可能是設計稿或頁面的文字(compare.text),反引號不能讓它跳出程式碼區段
                var arrow = $"{Code(d.Expected, MaxValueCell)} → {Code(d.Actual, MaxValueCell)}" +
                    (d.Delta is { } dl ? $" (Δ{dl})" : "");
                var sev = d.Soft ? $"{d.Severity.ToString().ToLowerInvariant()}(soft)" : d.Severity.ToString().ToLowerInvariant();
                var fix = FixHint.For(d, tokens) is { } f ? Code(f, MaxValueCell + 40) : "—";
                var cells = multi
                    ? $"| {Word(route)} | {Layer(n.DesignLayer, n.DesignId)} | {Word(d.Prop)} | {arrow} | {sev} | {fix} |"
                    : $"| {Layer(n.DesignLayer, n.DesignId)} | {Word(d.Prop)} | {arrow} | {sev} | {fix} |";
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
                sb.AppendLine($"- {(multi ? $"{Name(route)} " : "")}{Layer(u.DesignLayer, u.DesignId)} — {Word(u.Reason)}");
            sb.AppendLine();
        }

        // 多餘元件(compare.extras = "list" 且有發現才出現):只列出,不影響上面的結論。
        // selector 來自頁面:表格裡要跳脫 | 與換行、反引號另外處理,太長的從中間截斷
        var extras = reports
            .Where(r => r.Extras is { Total: > 0 })
            .Select(r => (r.Route, Extras: r.Extras!))
            .ToList();
        if (extras.Count > 0)
        {
            var extrasTotal = extras.Sum(x => x.Extras.Total);
            sb.AppendLine($"### Extra elements ({extrasTotal}) — on the page, but no node in the design (listed only, never gated)");
            sb.AppendLine();
            sb.AppendLine(multi ? "| Route | Element | Tag | Size and position |" : "| Element | Tag | Size and position |");
            sb.AppendLine(multi ? "|---|---|---|---|" : "|---|---|---|");
            foreach (var (route, x) in extras)
                foreach (var e in x.Items)
                {
                    var cells = $"{Code(e.Selector, MaxSelectorCell)} | {Code(e.Tag, 40)} | {ExtraBox.Describe(e.Box)} |";
                    sb.AppendLine(multi ? $"| {Word(route)} | {cells}" : $"| {cells}");
                }
            var unlisted = extras.Sum(x => x.Extras.Total - x.Extras.Items.Count);
            if (unlisted > 0)
            {
                sb.AppendLine();
                sb.AppendLine($"…and {unlisted} smaller one(s) not listed (each page keeps its largest).");
            }
            sb.AppendLine();
        }

        sb.AppendLine("<sub>Generated by Parity · property-level design fidelity checking</sub>");
        // 回報入口(2.0.0):只是連結,點了才開 GitHub 的誤判範本;報告片段常帶內部網址與內容,提醒先遮掉
        sb.AppendLine();
        sb.AppendLine($"<sub>Is a verdict wrong? [Report a misjudgment]({FeedbackLinks.Misjudgment}) — mask internal URLs and content before pasting any of this report.</sub>");
        return sb.ToString();
    }

    private static void AppendChangeList(StringBuilder sb, string title, IReadOnlyList<DiffRecord> items)
    {
        if (items.Count == 0) return;
        sb.AppendLine($"**{title}**");
        foreach (var d in items)
            sb.AppendLine($"- {Name(d.DesignLayer)} · {Word(d.Prop)} · {Code(d.Expected, MaxValueCell)} → {Code(d.Actual, MaxValueCell)}");
        sb.AppendLine();
    }

    /// <summary>圖層名這類名稱最多幾個字元:比表格的值寬鬆,正常的名字不會被截斷。</summary>
    internal const int MaxNameCell = 200;

    /// <summary>
    /// 來自資料的名稱(圖層名)放進 Markdown:一律是程式碼區段。GitHub 在區段內不處理連結、圖片、@ 提及與 HTML,
    /// 其他寫法都擋不住提及(它在 Markdown 解析之後才處理,反斜線沒有用)。
    /// 這些字串不是工具寫的:Figma 的編輯者、快照頁面的 id、report.json 與基準都改得到,而報告會貼成 PR 留言。
    /// </summary>
    public static string Name(string s) => Code(s, MaxNameCell);

    /// <summary>
    /// route、屬性名、未配對原因:平常是簡單的字,照原樣印,表格才不會滿是程式碼區段。有其他字元就放進程式碼區段。
    /// 允許的字元組不出連結、圖片或提及;開頭是 / 才准有點,否則 www.example.com 會被 GitHub 自動變成連結。
    /// </summary>
    public static string Word(string s) => PlainWord.IsMatch(s) ? s : Name(s);

    private static readonly Regex PlainWord = new(@"\A(?:/[A-Za-z0-9/.\-]*|[A-Za-z][A-Za-z0-9\-]*)\z", RegexOptions.CultureInvariant);

    /// <summary>表格裡的 selector 最多幾個字元(真實網站的結構路徑常有上百字元,表格會被撐爆)。</summary>
    internal const int MaxSelectorCell = 100;

    /// <summary>表格裡的期望值與實際值最多幾個字元。比報告裡的文字上限(120)長一點:正常情況不會再被截一次。</summary>
    internal const int MaxValueCell = 140;

    /// <summary>
    /// 來自頁面的字串放進表格的行內程式碼:控制字元(含換行)換成空白、太長的從中間截斷(頭尾都留:
    /// 開頭是最近的 id 錨點、結尾是元素本身)、<c>|</c> 跳脫。字串裡有反引號時,圍欄比最長的一段連續反引號多一個(CommonMark 的規則):
    /// 這裡的內容來自頁面或設計稿,跳出程式碼區段就能在 PR 留言裡寫任意 Markdown。區段內 GitHub 不處理 @ 提及與 HTML,不必另外跳脫。
    /// </summary>
    internal static string Code(string s, int max)
    {
        var flat = new string(s.Select(c => char.IsControl(c) ? ' ' : c).ToArray());
        if (flat.Length > max)
        {
            var head = max * 3 / 10;
            flat = flat[..head] + "…" + flat[^(max - head - 1)..];
        }
        flat = flat.Replace("|", "\\|");

        int longest = 0, run = 0;
        foreach (var c in flat)
        {
            run = c == '`' ? run + 1 : 0;
            longest = Math.Max(longest, run);
        }
        if (longest == 0) return $"`{flat}`";
        var fence = new string('`', longest + 1);
        return $"{fence} {flat} {fence}"; // 前後留空白:開頭或結尾是反引號時才不會黏在圍欄上
    }
}

/// <summary>多餘元件的框寫給人看(主控台與 Markdown 共用):<c>800×200 at (0, 0)</c>,四捨五入到整數 px。</summary>
public static class ExtraBox
{
    public static string Describe(Box b) => $"{N(b.W)}×{N(b.H)} at ({N(b.X)}, {N(b.Y)})";

    private static string N(double v)
    {
        var r = Math.Round(v, MidpointRounding.AwayFromZero);
        return (r == 0 ? 0 : r).ToString(System.Globalization.CultureInfo.InvariantCulture); // 不印出 "-0"
    }
}
