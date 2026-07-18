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
public sealed class DiffEngine(Tolerances tolerances)
{
    private readonly Tolerances _tol = tolerances;

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
        static double Variance(IEnumerable<double> xs)
        {
            var list = xs.ToList();
            var mean = list.Average();
            return list.Sum(v => (v - mean) * (v - mean)) / list.Count;
        }
    }

    /// <summary>嚴重度 = 超出容差的倍率:≤3x medium、≤6x serious、>6x critical。</summary>
    private static Severity SeverityFromRatio(double ratio)
        => ratio <= 3 ? Severity.Medium : ratio <= 6 ? Severity.Serious : Severity.Critical;

    private static string Fmt(double v) => v.ToString("0.##", CultureInfo.InvariantCulture);
}
