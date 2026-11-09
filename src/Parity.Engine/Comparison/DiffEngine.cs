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
        // 比的是等效內距:padding 由子元素的 margin 撐出來、畫面一樣時不報(EffectivePadding)。對不上時印的還是 CSS 的 padding
        if (d.Padding is { } pad)
        {
            var eff = EffectivePadding(pad, r);
            CompareNum(diffs, "paddingTop", pad.Top, eff.Top, _tol.SpacingPx);
            CompareNum(diffs, "paddingRight", pad.Right, eff.Right, _tol.SpacingPx);
            CompareNum(diffs, "paddingBottom", pad.Bottom, eff.Bottom, _tol.SpacingPx);
            CompareNum(diffs, "paddingLeft", pad.Left, eff.Left, _tol.SpacingPx);
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
            CompareTextStyle(diffs, d, r, dt, rt);
        }

        CompareTextContent(diffs, pair);
        CompareLineCount(diffs, pair);
        CompareBoxStyle(diffs, pair);

        // --- 顏色 ---
        if (d.Fill is { } fill && !fill.IsTransparent)
        {
            if (d.Type == DesignNodeType.Text)
            {
                if (r.Color is { } textColor) CompareColor(diffs, "color", fill, textColor);
            }
            else if (r.Background is { IsTransparent: false } own)
                CompareColor(diffs, "background", fill, own);
            // 讀不懂的顏色不是透明(2.5.0):以前會落到下面報「(transparent)」,是假落差。不比,主控台另外說有幾個顏色沒比
            else if (!r.HasUnreadColor("background"))
            {
                if (r.EffectiveBackground is { } inherited) CompareColor(diffs, "background", fill, inherited);
                else if (!r.HasUnreadColor("effectiveBackground"))
                    diffs.Add(new PropDiff("background", fill.ToHex(), "(transparent)", null,
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

                var order = ChildOrderDiff(matched, horizontal);
                if (order is not null)
                {
                    // root 沒有 NodeResult(隱含配對),掛在第一個子元件上
                    var owner = ReferenceEquals(parent, designRoot) ? matched[0].Design.Id : parent.Id;
                    DiffsOf(owner).Add(order);
                }
                if (IsAutoLayout(parent))
                    CompareAlignment(parent, rendered, kids, matched, horizontal, skipMainAxis: order is not null, DiffsOf);
            }
            else if (rendered is not null)
                CompareTextRelations(parent, rendered, renderedBy, DiffsOf);
            if (rendered is not null && !ReferenceEquals(parent, designRoot))
                CompareTextContainment(parent, rendered, renderedBy, DiffsOf);
            foreach (var c in parent.Children) Walk(c, renderedBy.GetValueOrDefault(c.Id));
        }
        Walk(designRoot, renderedRoot);
        return result;
    }

    /// <summary>
    /// 文字的相對關係(清單 #8,2.5.0)。文字刻意不比位移(Figma 的文字框 ≠ 瀏覽器的,比了狂誤報),所以文字被移走大多沒反應
    /// (論文評估快照模式只抓到 8.5%)。改比「誰在誰上面、誰在誰左邊」:同一層、至少一個是文字的兩個兄弟,
    /// 設計稿裡明確分開(隔 PositionPx 以上),頁面上卻明確反過來,才報。只是靠近、部分重疊都不報;只移動、關係沒變的照樣抓不到。
    /// 只在自由擺放的容器裡比:自動排版或有內距的容器已經比子元件順序(childOrder),再比就重複了。
    /// 每個節點最多一條,報在「相對父層位移比較大」的那一個(比較可能是被移動的)。軟落差。
    /// </summary>
    private void CompareTextRelations(
        DesignNode parent, RenderedNode rendered, Dictionary<string, RenderedNode> renderedBy, Func<string, List<PropDiff>> diffsOf)
    {
        var kids = parent.Children
            .Where(c => renderedBy.TryGetValue(c.Id, out var r) && !r.OutOfFlow
                && !string.Equals(c.LayoutPositioning, "ABSOLUTE", StringComparison.OrdinalIgnoreCase))
            .Select(c => (Design: c, Rendered: renderedBy[c.Id]))
            .ToList();
        if (kids.Count < 2 || kids.All(k => k.Design.Type != DesignNodeType.Text)) return;

        var g = _tol.PositionPx;
        // 相對父層的位移改變了多少:用來判斷是誰被移動
        double Moved((DesignNode Design, RenderedNode Rendered) k)
            => Math.Abs((k.Design.Box.X - parent.Box.X) - (k.Rendered.Box.X - rendered.Box.X))
             + Math.Abs((k.Design.Box.Y - parent.Box.Y) - (k.Rendered.Box.Y - rendered.Box.Y));
        var reported = new HashSet<string>(StringComparer.Ordinal);

        for (var i = 0; i < kids.Count; i++)
            for (var j = i + 1; j < kids.Count; j++)
            {
                var (a, b) = (kids[i], kids[j]);
                if (a.Design.Type != DesignNodeType.Text && b.Design.Type != DesignNodeType.Text) continue;
                if (Flipped(a.Design.Box, b.Design.Box, a.Rendered.Box, b.Rendered.Box, g) is not { } flipped) continue;
                var (expected, actual) = flipped;
                var (mover, other, flip) = Moved(b) > Moved(a) ? (b, a, true) : (a, b, false);
                if (!reported.Add(mover.Design.Id)) continue;
                SoftDiff(diffsOf(mover.Design.Id), "relation",
                    $"{(flip ? Opposite(expected) : expected)} {other.Design.Name}", $"{(flip ? Opposite(actual) : actual)} {other.Design.Name}");
            }
    }

    /// <summary>
    /// 設計稿裡 a 相對 b 明確在哪一邊、頁面上明確在相反那一邊:回傳 (設計上的關係, 頁面上的關係),例如 ("above", "below")。
    /// 「明確」= 兩個框之間隔 g 以上,不重疊。兩軸都沒反過來回 null。
    /// </summary>
    private static (string Expected, string Actual)? Flipped(Box da, Box db, Box ra, Box rb, double g)
    {
        bool Above(Box x, Box y) => x.Y + x.H + g <= y.Y;
        bool LeftOf(Box x, Box y) => x.X + x.W + g <= y.X;
        if (Above(da, db) && Above(rb, ra)) return ("above", "below");
        if (Above(db, da) && Above(ra, rb)) return ("below", "above");
        if (LeftOf(da, db) && LeftOf(rb, ra)) return ("left of", "right of");
        if (LeftOf(db, da) && LeftOf(ra, rb)) return ("right of", "left of");
        return null;
    }

    private static string Opposite(string relation) => relation switch
    {
        "above" => "below",
        "below" => "above",
        "left of" => "right of",
        _ => "left of",
    };

    /// <summary>
    /// 文字跑出容器(清單 #8):設計稿裡文字在父層的框裡,頁面上跟父層的框完全沒有交集。只是溢出一點(長文字)還有交集,不報。軟落差。
    /// 根(frame)不算父層:頁面的 body 跟設計 frame 的高度本來就常不一樣。
    /// 兩段以上、而且至少一半的文字同時跑出去:比較可能是父層自己配錯了元素,不是每段字都被移走(評估語料實測:
    /// 一個配錯的容器底下 13 段配對正確的字全被報出來),這時一條都不報。
    /// </summary>
    private void CompareTextContainment(
        DesignNode parent, RenderedNode rendered, Dictionary<string, RenderedNode> renderedBy, Func<string, List<PropDiff>> diffsOf)
    {
        if (parent.Box.W <= 0 || parent.Box.H <= 0) return;
        var texts = parent.Children
            .Where(c => c.Type == DesignNodeType.Text && renderedBy.TryGetValue(c.Id, out var r) && !r.OutOfFlow && !ReferenceEquals(r, rendered))
            .Select(c => (Design: c, Rendered: renderedBy[c.Id]))
            .ToList();
        var outside = texts.Where(t => Inside(t.Design.Box, parent.Box, _tol.SizePx) && !Intersects(t.Rendered.Box, rendered.Box)).ToList();
        if (outside.Count >= 2 && outside.Count * 2 >= texts.Count) return;
        foreach (var (child, _) in outside)
        {
            var diffs = diffsOf(child.Id);
            if (diffs.Any(d => d.Prop == "relation")) continue;
            SoftDiff(diffs, "relation", $"inside {parent.Name}", $"outside {parent.Name}");
        }

        static bool Inside(Box inner, Box outer, double tol)
            => inner.X >= outer.X - tol && inner.Y >= outer.Y - tol
            && inner.X + inner.W <= outer.X + outer.W + tol && inner.Y + inner.H <= outer.Y + outer.H + tol;
        static bool Intersects(Box x, Box y)
            => x.X < y.X + y.W && y.X < x.X + x.W && x.Y < y.Y + y.H && y.Y < x.Y + x.H;
    }

    private static bool IsAutoLayout(DesignNode n)
        => string.Equals(n.LayoutMode, "HORIZONTAL", StringComparison.OrdinalIgnoreCase)
        || string.Equals(n.LayoutMode, "VERTICAL", StringComparison.OrdinalIgnoreCase);

    /// <summary>明確寫了 FIXED。對齊只比這種:未知的尺寸(手寫 JSON)比下去,隨內容變動的距離就全是誤報。</summary>
    private static bool ExplicitlyFixed(string? sizing) => string.Equals(sizing, "FIXED", StringComparison.OrdinalIgnoreCase);

    /// <summary>
    /// 子元件順序:比閱讀順序的先後,用實際的框比,所以 CSS 的 order、row-reverse 自然算對。
    /// 同一排(交叉軸上重疊)比主軸,不同排比交叉軸。所以會換行的容器也比得了,而且兩邊在哪裡換行不一樣不算落差:
    /// 設計稿一排四張、頁面一排三張,只要先後一樣就對。差 1px 以內不算先後,疊在一起的元素不會互換。
    /// </summary>
    private static PropDiff? ChildOrderDiff(List<(DesignNode Design, RenderedNode Rendered)> kids, bool horizontal)
    {
        if (kids.Count < 2) return null;

        double MainStart(Box b) => horizontal ? b.X : b.Y;
        double CrossStart(Box b) => horizontal ? b.Y : b.X;
        double CrossEnd(Box b) => horizontal ? b.Y + b.H : b.X + b.W;
        int Relation(Box a, Box b)
        {
            var delta = CrossStart(a) < CrossEnd(b) && CrossStart(b) < CrossEnd(a)
                ? MainStart(b) - MainStart(a)
                : CrossStart(b) - CrossStart(a);
            return Math.Abs(delta) <= 1 ? 0 : Math.Sign(delta);
        }

        var inverted = false;
        for (var i = 0; i < kids.Count && !inverted; i++)
            for (var j = i + 1; j < kids.Count; j++)
                if (Relation(kids[i].Design.Box, kids[j].Design.Box) * Relation(kids[i].Rendered.Box, kids[j].Rendered.Box) < 0)
                {
                    inverted = true;
                    break;
                }
        if (!inverted) return null;

        // 閱讀順序:先分排,排內照主軸
        List<(DesignNode Design, RenderedNode Rendered)> InReadingOrder(Func<(DesignNode Design, RenderedNode Rendered), Box> box)
        {
            var row = 0;
            var rowEnd = double.NegativeInfinity;
            var ranked = new List<((DesignNode Design, RenderedNode Rendered) Kid, int Row)>();
            foreach (var k in kids.OrderBy(k => CrossStart(box(k))))
            {
                if (ranked.Count > 0 && CrossStart(box(k)) >= rowEnd)
                {
                    row++;
                    rowEnd = double.NegativeInfinity;
                }
                rowEnd = Math.Max(rowEnd, CrossEnd(box(k)));
                ranked.Add((k, row));
            }
            return ranked.OrderBy(r => r.Row).ThenBy(r => MainStart(box(r.Kid))).Select(r => r.Kid).ToList();
        }

        // 同名的子元件加上設計稿裡的序號,否則期望和實際看起來一模一樣
        var byDesign = InReadingOrder(k => k.Design.Box);
        var duplicated = byDesign.GroupBy(k => k.Design.Name).Any(g => g.Count() > 1);
        string Label((DesignNode Design, RenderedNode Rendered) k)
            => duplicated ? $"{k.Design.Name} #{byDesign.IndexOf(k) + 1}" : k.Design.Name;
        return new PropDiff("childOrder",
            Excerpt(string.Join(", ", byDesign.Select(Label))),
            Excerpt(string.Join(", ", InReadingOrder(k => k.Rendered.Box).Select(Label))),
            null, null, 0, Severity.Minor, Soft: true);
    }

    /// <summary>
    /// 等效內距:容器自己的 padding,某一邊對不上設計值時,看最靠近那一邊的子元素是不是用 margin 補上了差額 ——
    /// 設計稿(Figma auto-layout)的 padding 在頁面上常是子元素的 margin 撐出來的,畫面一樣,報了就是誤報。
    /// 那一邊要兩件事都成立才加上那段 margin,否則照舊用 CSS 的 padding:
    /// <list type="bullet">
    /// <item>padding + margin 在容差內等於設計值(只解釋差額,不拿 margin 去湊別的數字);</item>
    /// <item>那段 margin 真的在容器裡:子元素的邊框離容器的內距邊至少有 margin 那麼遠。區塊容器沒有 padding、border 時,
    /// 第一個子元素的上 margin 會穿出容器外(margin collapse),容器整個往下移、裡面沒有留白 —— 那不是內距。</item>
    /// </list>
    /// 「最靠近那一邊」用子元素的邊框算(不是 margin 框):兩個子元素一樣靠左、一個有 margin 一個沒有,貼邊的那個說了算。
    /// 只看參與版面的子元素(不算 absolute／fixed、零面積的);容器裡沒有子元素(文字直接寫在裡面)就沒有 margin 可以算。
    /// </summary>
    internal Insets EffectivePadding(Insets design, RenderedNode r)
    {
        var p = r.Padding;
        var b = r.BorderWidth ?? default;
        var kids = r.Children.Where(c => !c.OutOfFlow && c.Box.W > 0 && c.Box.H > 0).ToList();
        if (kids.Count == 0) return p;

        double Side(double expected, double padding, Func<RenderedNode, double> distance, Func<Insets, double> marginOf)
        {
            if (Math.Abs(expected - padding) <= _tol.SpacingPx) return padding;
            var edge = kids.MinBy(distance)!;
            var margin = marginOf(edge.Margin ?? default);
            return Math.Abs(expected - (padding + margin)) <= _tol.SpacingPx && distance(edge) >= margin - _tol.SpacingPx
                ? padding + margin
                : padding;
        }

        // 子元素邊框到容器內距邊(border 之內、padding 之內)的距離
        var top = Side(design.Top, p.Top, c => c.Box.Y - (r.Box.Y + b.Top + p.Top), m => m.Top);
        var right = Side(design.Right, p.Right, c => r.Box.X + r.Box.W - b.Right - p.Right - (c.Box.X + c.Box.W), m => m.Right);
        var bottom = Side(design.Bottom, p.Bottom, c => r.Box.Y + r.Box.H - b.Bottom - p.Bottom - (c.Box.Y + c.Box.H), m => m.Bottom);
        var left = Side(design.Left, p.Left, c => c.Box.X - (r.Box.X + b.Left + p.Left), m => m.Left);
        return new Insets(top, right, bottom, left);
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
        if (parent.Padding is { } pad && EffectivePadding(pad, rendered) is var eff
            && (Math.Abs(pad.Top - eff.Top) > _tol.SpacingPx || Math.Abs(pad.Right - eff.Right) > _tol.SpacingPx
                || Math.Abs(pad.Bottom - eff.Bottom) > _tol.SpacingPx || Math.Abs(pad.Left - eff.Left) > _tol.SpacingPx))
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

    /// <summary>斜體、底線與刪除線、大小寫、文字對齊。任一邊沒有這項資訊就不比。</summary>
    private static void CompareTextStyle(List<PropDiff> diffs, DesignNode d, RenderedNode r, Typography dt, Typography rt)
    {
        if (dt.FontStyle is { } style && rt.FontStyle is { } actualStyle && !Same(style, actualStyle))
            SoftDiff(diffs, "fontStyle", style, actualStyle);
        if (dt.TextDecoration is { } decoration && rt.TextDecoration is { } actualDecoration
            && !SameWords(decoration, actualDecoration))
            SoftDiff(diffs, "textDecoration", decoration, actualDecoration);
        if (dt.TextTransform is { } transform && rt.TextTransform is { } actualTransform && !Same(transform, actualTransform)
            && !LooksTheSame(d.Characters, r.Text, transform, actualTransform))
            SoftDiff(diffs, "textTransform", transform, actualTransform);
        if (dt.TextAlign is { } align && rt.TextAlign is { } actualAlign && !Same(align, actualAlign))
            SoftDiff(diffs, "textAlign", align, actualAlign);

        static bool Same(string a, string b) => a.Equals(b, StringComparison.OrdinalIgnoreCase);
        static bool SameWords(string a, string b)
            => a.Split(' ', StringSplitOptions.RemoveEmptyEntries).ToHashSet(StringComparer.OrdinalIgnoreCase)
                .SetEquals(b.Split(' ', StringSplitOptions.RemoveEmptyEntries));
    }

    /// <summary>
    /// 設計稿直接打大寫字母、頁面用 text-transform: uppercase(或反過來)時,畫面上是一樣的,不算落差。
    /// 只有轉換真的會改變那段文字才算;中文沒有大小寫,任何轉換都不會改變。
    /// </summary>
    private static bool LooksTheSame(string? designText, string? pageText, string designTransform, string pageTransform)
    {
        if (designText is { } dt && CssEffects.ApplyTextTransform(pageTransform, dt) == CssEffects.ApplyTextTransform(designTransform, dt)) return true;
        return pageText is { } pt && CssEffects.ApplyTextTransform(pageTransform, pt) == CssEffects.ApplyTextTransform(designTransform, pt);
    }

    /// <summary>
    /// 文字行數:抓「副標換行,下面全被推開」的根因。不比絕對座標,比行數。
    /// 期望值:快照有量到的就用它;其他用「框高 ÷ 行高」,文字框固定高度時框高跟行數無關,不比。舊快照的框含 padding,算不準,不比。
    /// </summary>
    private static void CompareLineCount(List<PropDiff> diffs, NodePair pair)
    {
        var (d, r) = (pair.Design, pair.Rendered);
        if (d.Type != DesignNodeType.Text || r.LineCount is not { } actual) return;
        var expected = d.LineCount;
        if (expected is null && pair.MatchedBy != "selector" && d.Text?.LineHeight is > 0
            && d.TextAutoResize?.ToUpperInvariant() is not ("NONE" or "TRUNCATE"))
            expected = Math.Max(1, (int)Math.Round(d.Box.H / d.Text.LineHeight!.Value));
        if (expected is { } e && e != actual)
            SoftDiff(diffs, "lineCount", e.ToString(CultureInfo.InvariantCulture), actual.ToString(CultureInfo.InvariantCulture), delta: Math.Abs(e - actual));
    }

    /// <summary>邊框粗細的容差比一般尺寸嚴:1px 的邊框有或沒有,一眼就看得出來。</summary>
    private const double BorderTolerance = 0.5;

    /// <summary>透明度的容差:0–1 的差。</summary>
    private const double OpacityTolerance = 0.01;

    /// <summary>陰影位移、模糊、擴散的容差(px)。</summary>
    private const double ShadowTolerance = 1;

    /// <summary>透明度、邊框、陰影、漸層。任一邊沒有這項資訊就不比,所以舊快照重跑不會多出落差。</summary>
    private void CompareBoxStyle(List<PropDiff> diffs, NodePair pair)
    {
        var (d, r) = (pair.Design, pair.Rendered);

        if (d.Opacity is { } opacity && r.Opacity is { } actualOpacity && Math.Abs(opacity - actualOpacity) > OpacityTolerance)
            SoftDiff(diffs, "opacity", Fmt(opacity), Fmt(actualOpacity), delta: Math.Round(Math.Abs(opacity - actualOpacity), 2), tolerance: OpacityTolerance);

        if (d.BorderWidth is { } designedWidth && r.BorderWidth is { } renderedWidth)
        {
            // 跟自己底色同色的邊框看不出來,算沒有邊框。對真實 Figma 實測抓到的:Ant Design 的設計稿邊框與底色同色,
            // 頁面是透明邊框,兩邊看起來一模一樣。文字節點的 Fill 是字色,不是底色,不算
            var width = d.Type != DesignNodeType.Text && BlendsIn(d.BorderColor, d.Fill) ? NoBorder : designedWidth;
            var actualWidth = BlendsIn(r.BorderColor, r.Background) ? NoBorder : renderedWidth;
            var delta = new[]
            {
                Math.Abs(width.Top - actualWidth.Top), Math.Abs(width.Right - actualWidth.Right),
                Math.Abs(width.Bottom - actualWidth.Bottom), Math.Abs(width.Left - actualWidth.Left),
            }.Max();
            if (delta > BorderTolerance)
                SoftDiff(diffs, "borderWidth", Describe(width), Describe(actualWidth), "px", Math.Round(delta, 2), BorderTolerance);
            else if (width != NoBorder && d.BorderColor is { } color && r.BorderColor is { } actualColor)
                CompareColorSoft(diffs, "borderColor", color, actualColor);
        }

        // 外框(CSS outline,2.5.0):不佔版面,以前完全沒比。跟邊框同一個形狀:先比粗細,一樣粗才比顏色;0 = 沒有外框。
        // 設計端不知道(Figma 的方框有 stroke、舊快照沒量過)就不比,見 DesignNode.OutlineWidth
        if (d.OutlineWidth is { } designedOutline && r.OutlineWidth is { } renderedOutline)
        {
            var delta = Math.Abs(designedOutline - renderedOutline);
            if (delta > BorderTolerance)
                SoftDiff(diffs, "outlineWidth", Fmt(designedOutline), Fmt(renderedOutline), "px", Math.Round(delta, 2), BorderTolerance);
            else if (designedOutline > 0 && d.OutlineColor is { } color && r.OutlineColor is { } actualColor)
                CompareColorSoft(diffs, "outlineColor", color, actualColor);
        }

        // 陰影或漸層裡有讀不懂的顏色:那一層會被當成不存在,比了就是假的「陰影不見」「漸層不見」,所以整項不比(2.5.0)
        var shadowField = d.Type == DesignNodeType.Text ? "textShadow" : "boxShadow";
        if (!r.HasUnreadColor(shadowField))
            CompareShadows(diffs, d.Shadows, d.Type == DesignNodeType.Text ? r.TextShadows : r.Shadows);
        if (!r.HasUnreadColor("backgroundImage"))
            CompareGradient(diffs, pair);
    }

    private static readonly Insets NoBorder = new(0, 0, 0, 0);

    private bool BlendsIn(Rgba? border, Rgba? fill)
        => border is { } b && fill is { IsTransparent: false } f && ColorDelta.Ciede2000(b, f) <= _tol.ColorDeltaE;

    /// <summary>
    /// 陰影。兩邊的順序不一定一樣(Figma 由下往上、CSS 由上往下),所以照「外陰影優先、模糊大的優先」排好再一對一比,
    /// 位移、模糊、擴散的差取最大。只有一邊有陰影算 missing。多出來的那幾層不比。
    /// </summary>
    private void CompareShadows(List<PropDiff> diffs, IReadOnlyList<ShadowSpec>? design, IReadOnlyList<ShadowSpec>? actual)
    {
        if (design is null || actual is null || (design.Count == 0 && actual.Count == 0)) return;
        if (design.Count == 0 || actual.Count == 0)
        {
            SoftDiff(diffs, "shadow", Describe(design), Describe(actual), status: DiffStatus.Missing);
            return;
        }

        var ds = Sorted(design);
        var rs = Sorted(actual);
        var delta = 0.0;
        var colorDelta = 0.0;
        (Rgba Expected, Rgba Actual)? worstColor = null;
        for (var i = 0; i < Math.Min(ds.Count, rs.Count); i++)
        {
            if (ds[i].Inset != rs[i].Inset)
            {
                SoftDiff(diffs, "shadow", Describe(design), Describe(actual), status: DiffStatus.Missing);
                return;
            }
            delta = Math.Max(delta, new[]
            {
                Math.Abs(ds[i].X - rs[i].X), Math.Abs(ds[i].Y - rs[i].Y),
                Math.Abs(ds[i].Blur - rs[i].Blur), Math.Abs(ds[i].Spread - rs[i].Spread),
            }.Max());
            var de = ColorDelta.Ciede2000(ds[i].Color, rs[i].Color);
            if (de > colorDelta) (colorDelta, worstColor) = (de, (ds[i].Color, rs[i].Color));
        }
        if (delta > ShadowTolerance)
            SoftDiff(diffs, "shadow", Describe(design), Describe(actual), delta: Math.Round(delta, 2), tolerance: ShadowTolerance);
        if (worstColor is { } w) CompareColorSoft(diffs, "shadowColor", w.Expected, w.Actual);

        static List<ShadowSpec> Sorted(IReadOnlyList<ShadowSpec> list)
            => [.. list.OrderBy(s => s.Inset).ThenByDescending(s => s.Blur).ThenBy(s => s.X).ThenBy(s => s.Y)];
    }

    /// <summary>
    /// 漸層:只有一邊有算 missing;兩邊都有就比頭尾色(取兩種配法裡差比較小的),再比種類與方向。
    /// 方向要兩邊都知道才比:「to top right」要看框的比例才知道角度,那種不比。
    /// 「設計稿是純色、頁面是漸層」只有設計稿明確是純色才算;舊快照沒記漸層,不能當作「沒有」。
    /// </summary>
    private void CompareGradient(List<PropDiff> diffs, NodePair pair)
    {
        var (d, r) = (pair.Design, pair.Rendered);
        if (d.Gradient is { } gradient)
        {
            if (r.Gradient is not { } actual)
            {
                SoftDiff(diffs, "gradient", Describe(gradient), NoGradient, status: DiffStatus.Missing);
                return;
            }
            if (gradient is { Start: { } a, End: { } b } && actual is { Start: { } c, End: { } e })
            {
                var straight = Math.Max(ColorDelta.Ciede2000(a, c), ColorDelta.Ciede2000(b, e));
                var flipped = Math.Max(ColorDelta.Ciede2000(a, e), ColorDelta.Ciede2000(b, c));
                var delta = Math.Min(straight, flipped);
                if (delta > _tol.ColorDeltaE)
                {
                    SoftDiff(diffs, "gradient", Describe(gradient), Describe(actual), delta: Math.Round(delta, 2), tolerance: _tol.ColorDeltaE);
                    return;
                }

                // 顏色對得上,再看種類與方向。這種落差沒有色差可分區間,只算條數
                if (gradient.Kind is { } kind && actual.Kind is { } actualKind && !kind.Equals(actualKind, StringComparison.OrdinalIgnoreCase))
                {
                    SoftDiff(diffs, "gradient", Describe(gradient), Describe(actual));
                    return;
                }
                // 頭尾對調再轉 180 度,畫出來是同一個漸層
                if (gradient.Angle is { } angle && actual.Angle is { } actualAngle
                    && !(straight <= _tol.ColorDeltaE && AngleGap(angle, actualAngle) <= GradientAngleTolerance)
                    && !(flipped <= _tol.ColorDeltaE && AngleGap(angle, actualAngle + 180) <= GradientAngleTolerance))
                    SoftDiff(diffs, "gradient", Describe(gradient), Describe(actual), tolerance: GradientAngleTolerance);
            }
        }
        else if (r.Gradient is { } only && d.Type != DesignNodeType.Text && pair.MatchedBy != "selector" && d.Fill is { IsTransparent: false })
        {
            SoftDiff(diffs, "gradient", NoGradient, Describe(only), status: DiffStatus.Missing);
        }
    }

    private const string NoGradient = "(no gradient)";

    /// <summary>漸層方向的容差(度)。</summary>
    private const double GradientAngleTolerance = 5;

    private static double AngleGap(double a, double b)
    {
        var gap = Math.Abs(a - b) % 360;
        return gap > 180 ? 360 - gap : gap;
    }

    private static string Describe(Insets w)
        => w.Top == w.Right && w.Right == w.Bottom && w.Bottom == w.Left
            ? Fmt(w.Top)
            : $"{Fmt(w.Top)} {Fmt(w.Right)} {Fmt(w.Bottom)} {Fmt(w.Left)}";

    private static string Describe(IReadOnlyList<ShadowSpec> shadows)
        => shadows.Count == 0 ? "none" : string.Join(", ", shadows.Select(s =>
            $"{(s.Inset ? "inset " : "")}{Fmt(s.X)}px {Fmt(s.Y)}px {Fmt(s.Blur)}px {Fmt(s.Spread)}px"));

    private static string Describe(GradientSpec g)
    {
        var colors = g is { Start: { } a, End: { } b } ? $"{a.ToHex()} → {b.ToHex()}" : "(gradient)";
        var kind = g.Kind is null || g.Kind.Equals("linear", StringComparison.OrdinalIgnoreCase) ? "" : g.Kind + " ";
        return g.Angle is { } angle ? $"{kind}{colors}, {Fmt(angle)}deg" : kind + colors;
    }

    private void CompareColorSoft(List<PropDiff> diffs, string prop, Rgba expected, Rgba actual)
    {
        var deltaE = ColorDelta.Ciede2000(expected, actual);
        if (deltaE > _tol.ColorDeltaE)
            SoftDiff(diffs, prop, expected.ToHex(), actual.ToHex(), delta: Math.Round(deltaE, 2), tolerance: _tol.ColorDeltaE);
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
