using System.Globalization;
using Parity.Engine.DesignSources;
using Parity.Engine.ImplementationSources;
using Parity.Engine.Model;

namespace Parity.Engine.Comparison;

/// <summary>
/// 相對位置比對的參照:父層(設計/實作)的框 + 同父層下其他「已配對」兄弟(設計節點 + 實作框)。
/// 只給非 auto-layout 父層的子節點(auto-layout 位置由 padding/gap 決定,那邊已有比對)。
/// </summary>
public sealed record PositionContext(
    Box DesignParent, Box RenderedParent,
    IReadOnlyList<(DesignSources.DesignNode Design, Box Rendered)> MatchedSiblings);

/// <summary>
/// 數值級 diff(規畫書 4.8)。只比「不管版面怎麼流動都該一樣」的東西:
///   - 元素自身尺寸(w/h;TEXT 節點例外——Figma 文字框 vs DOM 行內框天生不同,比了狂誤報)
///   - 內距四邊、auto-layout itemSpacing ↔ 實際子元素 gap
///   - 字體:size/weight/line-height/letter-spacing 精確比;font-family 當「軟落差」
///   - 顏色:CIEDE2000 ΔE 設門檻
///   - 相對位置(offsetX/offsetY):相對「最近的已配對兄弟」或父層邊的偏移(規畫書 4.8 的
///     「比相對位置」)。參照優先用兄弟——一個元素壞掉才不會讓後面整排連環誤報。
/// 不比絕對位置 x/y——彈性版面下本來就會不同,比了 = 狂誤報 = 失去信任。
/// </summary>
public sealed class DiffEngine(Tolerances tolerances, TextComparison compareText = TextComparison.Soft)
{
    private readonly Tolerances _tol = tolerances;
    private readonly TextComparison _compareText = compareText;

    /// <summary><see cref="RenderedNode.TextContent"/> 的長度上限(與 CaptureScript 同值):到上限代表被截斷,沒看完就不判。</summary>
    internal const int TextContentLimit = 2000;

    /// <summary>報告裡的文字最多幾個字元:report.json 會被推到伺服器、貼進 PR 留言,整段文字不用都帶。</summary>
    internal const int MaxTextInReport = 120;

    /// <summary>這些元素的文字不在子節點裡(值、替代文字、畫布),比了都是誤報。</summary>
    private static readonly HashSet<string> NoTextTags = new(StringComparer.OrdinalIgnoreCase)
    {
        "input", "textarea", "select", "img", "svg", "canvas", "video", "audio", "iframe", "object", "embed", "picture",
    };

    public NodeResult Diff(NodePair pair, PositionContext? position = null)
    {
        var diffs = new List<PropDiff>();
        var d = pair.Design;
        var r = pair.Rendered;

        // --- 相對位置(有參照才比;auto-layout 父層的子節點不給參照)---
        // TEXT 不當目標:inline/置中的 DOM 文字框位置 ≠ Figma 文字框,比了必誤報;
        // 文字跑位通常會由它的容器現形。
        if (position is { } pos && d.Type != DesignNodeType.Text)
        {
            var (expX, actX) = HorizontalOffset(d.Box, r.Box, pos);
            CompareNum(diffs, "offsetX", expX, actX, _tol.PositionPx);
            if (VerticalOffset(d.Box, r.Box, pos) is { } vy)
                CompareNum(diffs, "offsetY", vy.Expected, vy.Actual, _tol.PositionPx);
        }

        // --- 尺寸(TEXT 節點跳過,避免文字框量測差異誤報)---
        // 只比「設計上固定」的那一軸:HUG(隨內容)/FILL(隨父層)的尺寸不是設計約束,
        // 比了會因 Figma 量測 ≠ 瀏覽器渲染而誤報(例如 auto-layout 按鈕寬度)。
        if (d.Type != DesignNodeType.Text)
        {
            if (IsFixedSize(d.LayoutSizingHorizontal))
                CompareNum(diffs, "width", d.Box.W, r.Box.W, _tol.SizePx);
            if (IsFixedSize(d.LayoutSizingVertical))
                CompareNum(diffs, "height", d.Box.H, r.Box.H, _tol.SizePx);
        }

        // --- 內距(設計端有 auto-layout padding 才比)---
        if (d.Padding is { } pad)
        {
            CompareNum(diffs, "paddingTop", pad.Top, r.Padding.Top, _tol.SpacingPx);
            CompareNum(diffs, "paddingRight", pad.Right, r.Padding.Right, _tol.SpacingPx);
            CompareNum(diffs, "paddingBottom", pad.Bottom, r.Padding.Bottom, _tol.SpacingPx);
            CompareNum(diffs, "paddingLeft", pad.Left, r.Padding.Left, _tol.SpacingPx);
        }

        // --- itemSpacing:設計 auto-layout gap ↔ 實際相鄰子元素間距 ---
        if (d.ItemSpacing is { } spacing && d.Children.Count >= 2 && r.Children.Count >= 2)
        {
            var actualGap = MeanChildGap(r, d.LayoutMode);
            if (actualGap is { } gap)
                CompareNum(diffs, "itemSpacing", spacing, gap, _tol.SpacingPx);
        }

        // --- 圓角 ---
        if (d.CornerRadius is { } radius && r.CornerRadius is { } actualRadius)
            CompareNum(diffs, "cornerRadius", radius, actualRadius, _tol.SizePx);

        // --- 字體 ---
        if (d.Type == DesignNodeType.Text && d.Text is { } dt && r.Typography is { } rt)
        {
            if (dt.FontSize is { } fs && rt.FontSize is { } rfs)
                CompareNum(diffs, "fontSize", fs, rfs, _tol.FontSizePx);
            if (dt.FontWeight is { } fw && rt.FontWeight is { } rfw)
                CompareFontWeight(diffs, fw, rfw);
            if (dt.LineHeight is { } lh && rt.LineHeight is { } rlh)
                CompareNum(diffs, "lineHeight", lh, rlh, _tol.SizePx);
            if (dt.LetterSpacing is { } ls && rt.LetterSpacing is { } rls)
                CompareNum(diffs, "letterSpacing", ls, rls, _tol.FontSizePx);
            if (dt.FontFamily is { } ff && rt.FontFamily is { } rff)
                CompareFontFamily(diffs, ff, rff); // 軟落差(Figma 名 ≠ CSS font stack)
        }

        CompareTextContent(diffs, pair);

        // --- 顏色 ---
        if (d.Fill is { } fill && !fill.IsTransparent)
        {
            if (d.Type == DesignNodeType.Text)
            {
                if (r.Color is { } textColor) CompareColor(diffs, "color", fill, textColor);
            }
            else
            {
                var bg = r.Background is { IsTransparent: false } b ? b : r.EffectiveBackground;
                if (bg is { } actualBg) CompareColor(diffs, "background", fill, actualBg);
                else diffs.Add(new PropDiff("background", fill.ToHex(), "(transparent)", null,
                    null, _tol.ColorDeltaE, Severity.Medium, DiffStatus.Missing));
            }
        }

        var severity = diffs.Count == 0 ? Severity.None : diffs.Max(x => x.Severity);
        return new NodeResult(d.Name, d.Id, r.Selector, pair.MatchedBy, severity, diffs, d.Box, r.Box);
    }

    /// <summary>
    /// 要看父層與兄弟才算得出來的落差,依設計節點 Id 分組:自動排版容器裡的子元件順序(childOrder)與對齊(mainAxisOffset、crossAxisOffset)。
    /// 自動排版的子元件不比相對位置(那會把 padding、gap 的同一個根因報兩次),所以順序對調、對齊跑掉原本完全看不到。
    /// 都是軟落差。呼叫端在視窗寬度與設計稿不同時不要呼叫:版面可能合法重排。
    /// </summary>
    public Dictionary<string, List<PropDiff>> StructuralDiffs(
        DesignNode designRoot, RenderedNode renderedRoot, IReadOnlyList<NodePair> pairs)
    {
        var renderedBy = new Dictionary<string, RenderedNode>();
        foreach (var p in pairs) renderedBy.TryAdd(p.Design.Id, p.Rendered);

        var result = new Dictionary<string, List<PropDiff>>();
        List<PropDiff> DiffsOf(string id) => result.TryGetValue(id, out var list) ? list : result[id] = [];

        void Walk(DesignNode parent, RenderedNode? rendered)
        {
            if (rendered is not null && (IsAutoLayout(parent) || parent.Padding is not null))
            {
                // 不參與版面流的子節點不算;順序照設計稿的位置排
                var kids = parent.Children
                    .Where(c => !string.Equals(c.LayoutPositioning, "ABSOLUTE", StringComparison.OrdinalIgnoreCase))
                    .Where(c => !renderedBy.TryGetValue(c.Id, out var r) || !r.OutOfFlow)
                    .ToList();
                var matched = kids.Where(c => renderedBy.ContainsKey(c.Id)).Select(c => (Design: c, Rendered: renderedBy[c.Id])).ToList();
                var horizontal = IsAutoLayout(parent)
                    ? string.Equals(parent.LayoutMode, "HORIZONTAL", StringComparison.OrdinalIgnoreCase)
                    : matched.Count > 1
                        && Variance(matched.Select(m => m.Design.Box.X)) > Variance(matched.Select(m => m.Design.Box.Y));

                var order = ChildOrderDiff(parent, matched, horizontal);
                if (order is not null)
                {
                    // root 沒有 NodeResult(隱含配對),掛在第一個子元件上
                    var owner = ReferenceEquals(parent, designRoot) ? matched[0].Design.Id : parent.Id;
                    DiffsOf(owner).Add(order);
                }
                if (IsAutoLayout(parent))
                    CompareAlignment(parent, rendered, kids, matched, horizontal, skipMainAxis: order is not null, DiffsOf);
            }
            foreach (var c in parent.Children) Walk(c, renderedBy.GetValueOrDefault(c.Id));
        }
        Walk(designRoot, renderedRoot);
        return result;
    }

    private static bool IsAutoLayout(DesignNode n)
        => string.Equals(n.LayoutMode, "HORIZONTAL", StringComparison.OrdinalIgnoreCase)
        || string.Equals(n.LayoutMode, "VERTICAL", StringComparison.OrdinalIgnoreCase);

    /// <summary>明確寫了 FIXED。對齊只比這種:未知的尺寸(手寫 JSON)比下去,隨內容變動的距離就全是誤報。</summary>
    private static bool ExplicitlyFixed(string? sizing) => string.Equals(sizing, "FIXED", StringComparison.OrdinalIgnoreCase);

    /// <summary>
    /// 子元件順序:沿主軸比設計稿與頁面上的先後,用實際的框比,所以 CSS 的 order、row-reverse 自然算對。
    /// 這些情況不比:會換行的容器(第一版)、頁面上換了行(子元件在交叉軸上不重疊,先後沒有意義)、配到的子元件不到兩個。
    /// 差 1px 以內不算先後,疊在一起的元素不會互換。
    /// </summary>
    private static PropDiff? ChildOrderDiff(DesignNode parent, List<(DesignNode Design, RenderedNode Rendered)> kids, bool horizontal)
    {
        if (kids.Count < 2 || string.Equals(parent.LayoutWrap, "WRAP", StringComparison.OrdinalIgnoreCase)) return null;

        double Start(Box b) => horizontal ? b.X : b.Y;
        var (firstStart, firstEnd) = CrossSpan(kids[0].Rendered.Box);
        (double Start, double End) CrossSpan(Box b) => horizontal ? (b.Y, b.Y + b.H) : (b.X, b.X + b.W);
        foreach (var (_, r) in kids.Skip(1))
        {
            var (s, e) = CrossSpan(r.Box);
            if (s >= firstEnd || e <= firstStart) return null;
        }

        var inverted = false;
        for (var i = 0; i < kids.Count && !inverted; i++)
            for (var j = i + 1; j < kids.Count; j++)
            {
                var designDelta = Start(kids[j].Design.Box) - Start(kids[i].Design.Box);
                var renderedDelta = Start(kids[j].Rendered.Box) - Start(kids[i].Rendered.Box);
                if ((designDelta > 1 && renderedDelta < -1) || (designDelta < -1 && renderedDelta > 1))
                {
                    inverted = true;
                    break;
                }
            }
        if (!inverted) return null;

        // 同名的子元件加上設計稿裡的序號,否則期望和實際看起來一模一樣
        var byDesign = kids.OrderBy(k => Start(k.Design.Box)).ToList();
        var duplicated = byDesign.GroupBy(k => k.Design.Name).Any(g => g.Count() > 1);
        string Label((DesignNode Design, RenderedNode Rendered) k)
            => duplicated ? $"{k.Design.Name} #{byDesign.IndexOf(k) + 1}" : k.Design.Name;
        return new PropDiff("childOrder",
            Excerpt(string.Join(", ", byDesign.Select(Label))),
            Excerpt(string.Join(", ", kids.OrderBy(k => Start(k.Rendered.Box)).Select(Label))),
            null, null, 0, Severity.Minor, Soft: true);
    }

    /// <summary>
    /// 容器對齊:比子元件到父層邊的距離。只比「距離應該固定」的情況,其他原因造成的位移已經有別條落差,不重複報:
    /// 父層尺寸或 padding 不一樣就整個跳過;交叉軸要父層與子元件都明確是 FIXED,而且子元件尺寸沒差;
    /// 主軸只比第一個子元件、只比 MIN/CENTER/MAX,CENTER/MAX 還要所有子元件都配到、都是固定尺寸,總長度也沒差。
    /// 文字節點不比:文字框跟瀏覽器量的本來就不同。
    /// </summary>
    private void CompareAlignment(
        DesignNode parent, RenderedNode rendered, List<DesignNode> kids,
        List<(DesignNode Design, RenderedNode Rendered)> matched, bool horizontal, bool skipMainAxis,
        Func<string, List<PropDiff>> diffsOf)
    {
        if (parent.Padding is { } pad
            && (Math.Abs(pad.Top - rendered.Padding.Top) > _tol.SpacingPx || Math.Abs(pad.Right - rendered.Padding.Right) > _tol.SpacingPx
                || Math.Abs(pad.Bottom - rendered.Padding.Bottom) > _tol.SpacingPx || Math.Abs(pad.Left - rendered.Padding.Left) > _tol.SpacingPx))
            return;

        double Pos(Box b, bool mainAxis) => horizontal == mainAxis ? b.X : b.Y;
        double Size(Box b, bool mainAxis) => horizontal == mainAxis ? b.W : b.H;
        string? Sizing(DesignNode n, bool mainAxis) => horizontal == mainAxis ? n.LayoutSizingHorizontal : n.LayoutSizingVertical;
        bool SameSize(Box a, Box b, bool mainAxis) => Math.Abs(Size(a, mainAxis) - Size(b, mainAxis)) <= _tol.SizePx;
        void Offset(DesignNode child, Box actualBox, bool mainAxis)
        {
            var expected = Pos(child.Box, mainAxis) - Pos(parent.Box, mainAxis);
            var actual = Pos(actualBox, mainAxis) - Pos(rendered.Box, mainAxis);
            var delta = Math.Abs(expected - actual);
            if (delta > _tol.PositionPx)
                diffsOf(child.Id).Add(new PropDiff(mainAxis ? "mainAxisOffset" : "crossAxisOffset",
                    Fmt(expected), Fmt(actual), "px", Math.Round(delta, 2), _tol.PositionPx, Severity.Minor, Soft: true));
        }

        // 交叉軸:每個子元件各比一次
        if (ExplicitlyFixed(Sizing(parent, false)) && SameSize(parent.Box, rendered.Box, false)
            && !string.Equals(parent.CounterAxisAlignItems, "BASELINE", StringComparison.OrdinalIgnoreCase))
            foreach (var (d, r) in matched)
                if (d.Type != DesignNodeType.Text && ExplicitlyFixed(Sizing(d, false)) && SameSize(d.Box, r.Box, false))
                    Offset(d, r.Box, false);

        // 主軸:第一個子元件。順序對調時已經報了 childOrder,這裡的距離沒有意義
        if (skipMainAxis || matched.Count == 0
            || parent.PrimaryAxisAlignItems?.ToUpperInvariant() is not ("MIN" or "CENTER" or "MAX")
            || !ExplicitlyFixed(Sizing(parent, true)) || !SameSize(parent.Box, rendered.Box, true))
            return;
        var byDesign = matched.OrderBy(m => Pos(m.Design.Box, true)).ToList();
        var (first, firstRendered) = byDesign[0];
        if (first.Type == DesignNodeType.Text) return;
        if (!parent.PrimaryAxisAlignItems.Equals("MIN", StringComparison.OrdinalIgnoreCase))
        {
            // 置中、靠尾:第一個子元件的位置取決於所有子元件的總長度,長度不一樣時位移是別的原因
            if (matched.Count != kids.Count || byDesign.Any(m => m.Design.Type == DesignNodeType.Text || !ExplicitlyFixed(Sizing(m.Design, true)))) return;
            var (last, lastRendered) = byDesign[^1];
            var designSpan = Pos(last.Box, true) + Size(last.Box, true) - Pos(first.Box, true);
            var renderedSpan = Pos(lastRendered.Box, true) + Size(lastRendered.Box, true) - Pos(firstRendered.Box, true);
            if (Math.Abs(designSpan - renderedSpan) > _tol.SizePx) return;
        }
        Offset(first, firstRendered.Box, true);
    }

    /// <summary>
    /// 水平參照:「同排(垂直重疊)、右緣可靠、最靠近的左方兄弟」的右緣(取 gap),
    /// 沒有就父層左緣(取 offset)。三個條件各擋一種誤報:
    ///   - 同一個兄弟兩邊都用 → 量同一段距離;中間元素偏掉時,鄰距沒變的不會被連坐
    ///   - 右緣可靠 = 非 TEXT 且寬度 FIXED——文字/HUG 的 DOM 寬 ≠ Figma 框寬,拿它右緣必誤報
    ///   - 同排(垂直重疊)——不同排的元素拿來當水平參照沒有意義
    /// </summary>
    private static (double Expected, double Actual) HorizontalOffset(Box d, Box r, PositionContext pos)
    {
        (DesignNode Design, Box Rendered)? best = null;
        foreach (var s in pos.MatchedSiblings)
        {
            var sb = s.Design.Box;
            if (sb.X + sb.W > d.X + 0.5) continue;                 // 不在左方
            if (sb.Y >= d.Y + d.H || sb.Y + sb.H <= d.Y) continue; // 不同排
            if (!ReliableWidth(s.Design)) continue;
            if (best is null || sb.X + sb.W > best.Value.Design.Box.X + best.Value.Design.Box.W)
                best = s;
        }
        return best is { } b
            ? (d.X - (b.Design.Box.X + b.Design.Box.W), r.X - (b.Rendered.X + b.Rendered.W))
            : (d.X - pos.DesignParent.X, r.X - pos.RenderedParent.X);
    }

    /// <summary>
    /// 垂直參照:同列(水平重疊)、下緣可靠、最靠近的上方兄弟;都沒有才用父層上緣。
    /// 與水平不同的一點:上方**存在**兄弟但全都高度不可靠(文字/HUG)→ 回傳 null 跳過——
    /// 這種節點的垂直位置是「流經那些不可靠高度」累積出來的(網頁主流是垂直 flow),
    /// 拿父層邊比會把文字行高的自然差異累積成誤報。X 沒有這個問題(垂直 flow 不累積 X)。
    /// </summary>
    private static (double Expected, double Actual)? VerticalOffset(Box d, Box r, PositionContext pos)
    {
        (DesignNode Design, Box Rendered)? best = null;
        var unreliableAbove = false;
        foreach (var s in pos.MatchedSiblings)
        {
            var sb = s.Design.Box;
            if (sb.Y + sb.H > d.Y + 0.5) continue;                 // 不在上方
            if (sb.X >= d.X + d.W || sb.X + sb.W <= d.X) continue; // 不同列
            if (!ReliableHeight(s.Design)) { unreliableAbove = true; continue; }
            if (best is null || sb.Y + sb.H > best.Value.Design.Box.Y + best.Value.Design.Box.H)
                best = s;
        }
        if (best is { } b)
            return (d.Y - (b.Design.Box.Y + b.Design.Box.H), r.Y - (b.Rendered.Y + b.Rendered.H));
        if (unreliableAbove) return null;
        return (d.Y - pos.DesignParent.Y, r.Y - pos.RenderedParent.Y);
    }

    private static bool ReliableWidth(DesignNode n)
        => n.Type != DesignNodeType.Text && IsFixedSize(n.LayoutSizingHorizontal);

    private static bool ReliableHeight(DesignNode n)
        => n.Type != DesignNodeType.Text && IsFixedSize(n.LayoutSizingVertical);

    /// <summary>尺寸該不該比:HUG/FILL 由內容或父層決定 → 不比;FIXED 或未知(手寫 JSON)→ 比。</summary>
    private static bool IsFixedSize(string? sizing)
        => !string.Equals(sizing, "HUG", StringComparison.OrdinalIgnoreCase)
        && !string.Equals(sizing, "FILL", StringComparison.OrdinalIgnoreCase);

    // ---------- 個別比較 ----------

    private static void CompareNum(List<PropDiff> diffs, string prop, double expected, double actual, double tol)
    {
        var delta = Math.Abs(expected - actual);
        if (delta <= tol) return;
        diffs.Add(new PropDiff(prop, Fmt(expected), Fmt(actual), "px",
            Math.Round(delta, 2), tol, SeverityFromRatio(delta / Math.Max(tol, 0.0001))));
    }

    private static void CompareFontWeight(List<PropDiff> diffs, double expected, double actual)
    {
        if (Math.Abs(expected - actual) < 1) return;
        // 100 級距差一級算 medium,差兩級以上 serious
        var severity = Math.Abs(expected - actual) >= 200 ? Severity.Serious : Severity.Medium;
        diffs.Add(new PropDiff("fontWeight", Fmt(expected), Fmt(actual), null,
            Math.Abs(expected - actual), 0, severity));
    }

    private static void CompareFontFamily(List<PropDiff> diffs, string designFamily, string cssStack)
    {
        // Figma 給單一字型名,CSS 是 font stack("Inter, sans-serif")→ 只要 stack 含該字型就算過
        var families = cssStack.Split(',')
            .Select(f => f.Trim().Trim('"', '\''))
            .ToList();
        if (families.Any(f => f.Equals(designFamily.Trim(), StringComparison.OrdinalIgnoreCase)))
            return;
        diffs.Add(new PropDiff("fontFamily", designFamily, cssStack, null,
            null, 0, Severity.Minor, Soft: true)); // 軟落差,不擋 gate
    }

    /// <summary>軟落差:會列出、不扣分、永遠不擋、--baseline 不算新增。新的比對項目都先做成這樣,真實網站測過誤報再改。</summary>
    private static void SoftDiff(List<PropDiff> diffs, string prop, string expected, string actual,
        string? unit = null, double? delta = null, double tolerance = 0, DiffStatus status = DiffStatus.Mismatch)
        => diffs.Add(new PropDiff(prop, expected, actual, unit, delta, tolerance, Severity.Minor, status, Soft: true));

    /// <summary>
    /// 文字內容(設計稿的 TEXT 節點 ↔ 頁面上的文字)。文字配對配上的本來就一樣,這條抓的是靠圖層名配上、文字卻不一樣的。
    /// 比法與 text-not-found 相同:忽略大小寫(text-transform)、所有空白與看不見的字元,彎引號當直引號。
    /// 元素有子元素時,設計稿的文字在子孫文字裡就算對(圖層名可能配到外層容器,內容不只這一段)。
    /// </summary>
    private void CompareTextContent(List<PropDiff> diffs, NodePair pair)
    {
        var (d, r) = (pair.Design, pair.Rendered);
        if (_compareText == TextComparison.None || d.Type != DesignNodeType.Text || string.IsNullOrWhiteSpace(d.Characters)) return;
        // 手動對應:設計系統常用佔位文字,會手動對應通常就是因為這樣,比了全是誤報(README「設計系統」那段)
        if (pair.MatchedBy == "explicit") return;
        // 快照基準:動態內容每次都不同(野生實查 F4),預設不比;明說 gate 才比
        if (pair.MatchedBy == "selector" && _compareText != TextComparison.Gate) return;
        if (NoTextTags.Contains(r.Tag)) return;

        var expected = Matcher.TextFingerprint(d.Characters!);
        if (r.Text is { } own && Matcher.TextFingerprint(own).Equals(expected, StringComparison.OrdinalIgnoreCase)) return;
        if (r.TextContent is { } full)
        {
            if (Matcher.TextFingerprint(full).Contains(expected, StringComparison.OrdinalIgnoreCase)) return;
            if (full.Length >= TextContentLimit) return;
        }

        var actual = r.TextContent ?? r.Text;
        var missing = string.IsNullOrWhiteSpace(actual);
        var shownExpected = Excerpt(d.Characters!);
        var shownActual = missing ? "(no text)" : Excerpt(actual!);
        if (_compareText == TextComparison.Gate)
            diffs.Add(new PropDiff("text", shownExpected, shownActual, null, null, 0, Severity.Serious,
                missing ? DiffStatus.Missing : DiffStatus.Mismatch));
        else
            SoftDiff(diffs, "text", shownExpected, shownActual, status: missing ? DiffStatus.Missing : DiffStatus.Mismatch);
    }

    /// <summary>報告用的文字:壓空白、太長的截斷。</summary>
    private static string Excerpt(string s)
    {
        var flat = Matcher.NormalizeText(s);
        return flat.Length <= MaxTextInReport ? flat : flat[..(MaxTextInReport - 1)] + "…";
    }

    private void CompareColor(List<PropDiff> diffs, string prop, Rgba expected, Rgba actual)
    {
        var deltaE = ColorDelta.Ciede2000(expected, actual);
        if (deltaE <= _tol.ColorDeltaE) return;
        diffs.Add(new PropDiff(prop, expected.ToHex(), actual.ToHex(), null,
            Math.Round(deltaE, 2), _tol.ColorDeltaE, SeverityFromRatio(deltaE / _tol.ColorDeltaE)));
    }

    /// <summary>相鄰子元素的平均 gap。主軸依設計 layoutMode;沒給就取子元素排列方向猜。</summary>
    internal static double? MeanChildGap(RenderedNode parent, string? layoutMode)
    {
        var kids = parent.Children.Where(c => c.Box is { W: > 0, H: > 0 }).ToList();
        if (kids.Count < 2) return null;

        bool horizontal = layoutMode is not null
            ? layoutMode.Equals("HORIZONTAL", StringComparison.OrdinalIgnoreCase)
            : VarianceX(kids) > VarianceY(kids);

        var ordered = horizontal
            ? kids.OrderBy(k => k.Box.X).ToList()
            : kids.OrderBy(k => k.Box.Y).ToList();

        var gaps = new List<double>();
        for (var i = 1; i < ordered.Count; i++)
        {
            var prev = ordered[i - 1].Box;
            var cur = ordered[i].Box;
            gaps.Add(horizontal ? cur.X - (prev.X + prev.W) : cur.Y - (prev.Y + prev.H));
        }
        return gaps.Count > 0 ? gaps.Average() : null;

        static double VarianceX(List<RenderedNode> ns) => Variance(ns.Select(n => n.Box.X));
        static double VarianceY(List<RenderedNode> ns) => Variance(ns.Select(n => n.Box.Y));
    }

    private static double Variance(IEnumerable<double> xs)
    {
        var list = xs.ToList();
        var mean = list.Average();
        return list.Sum(v => (v - mean) * (v - mean)) / list.Count;
    }

    /// <summary>嚴重度 = 超出容差的倍率:≤3x medium、≤6x serious、>6x critical。</summary>
    private static Severity SeverityFromRatio(double ratio)
        => ratio <= 3 ? Severity.Medium : ratio <= 6 ? Severity.Serious : Severity.Critical;

    private static string Fmt(double v) => v.ToString("0.##", CultureInfo.InvariantCulture);
}
