using System.Globalization;
using Parity.Engine.DesignSources;
using Parity.Engine.ImplementationSources;
using Parity.Engine.Model;

namespace Parity.Engine.Comparison;

/// <summary>
/// 數值級 diff(規畫書 4.8)。只比「不管版面怎麼流動都該一樣」的東西:
///   - 元素自身尺寸(w/h;TEXT 節點例外——Figma 文字框 vs DOM 行內框天生不同,比了狂誤報)
///   - 內距四邊、auto-layout itemSpacing ↔ 實際子元素 gap
///   - 字體:size/weight/line-height/letter-spacing 精確比;font-family 當「軟落差」
///   - 顏色:CIEDE2000 ΔE 設門檻
/// 預設不比絕對位置 x/y——彈性版面下本來就會不同,比了 = 狂誤報 = 失去信任。
/// </summary>
public sealed class DiffEngine(Tolerances tolerances)
{
    private readonly Tolerances _tol = tolerances;

    public NodeResult Diff(NodePair pair)
    {
        var diffs = new List<PropDiff>();
        var d = pair.Design;
        var r = pair.Rendered;

        // --- 尺寸(TEXT 節點跳過,避免文字框量測差異誤報)---
        if (d.Type != DesignNodeType.Text)
        {
            CompareNum(diffs, "width", d.Box.W, r.Box.W, _tol.SizePx);
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
