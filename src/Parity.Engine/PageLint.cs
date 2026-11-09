using Parity.Engine.Comparison;
using Parity.Engine.ImplementationSources;
using Parity.Engine.Model;

namespace Parity.Engine;

/// <summary>
/// 偏離 token 的程度(2.5.0,競品清單「實作端 token 合規、near-miss 分級」;兩種 lint 共用)。
/// <list type="bullet">
/// <item><b>near miss</b>:離某個 token 很近——幾乎一定是手滑(#2564EB 寫成 #2563EB、15px 寫成 16px),附「改成哪個 token」。</item>
/// <item><b>off token</b>:附近沒有 token——可能是故意的、第三方元件,或 token 檔少收了一個值。</item>
/// </list>
/// </summary>
public static class TokenDistance
{
    /// <summary>顏色在容差之外、ΔE 不超過這個值算 near miss。ΔE 6 左右是「並排看得出來,單看分不出」的程度。</summary>
    public const double NearColorDeltaE = 6;

    /// <summary>尺寸差不超過這個 px、也不超過值的四分之一,算 near miss(16 寫成 15 是,4 寫成 6 不是)。</summary>
    public const double NearSizePx = 2;

    public static bool IsNearMiss(bool isColor, double distance, double value)
        => isColor ? distance <= NearColorDeltaE : distance <= NearSizePx && distance <= Math.Abs(value) / 4;
}

/// <summary>頁面上一個不在 token 裡的值:用在哪個屬性、幾個元素、舉幾個例子,以及最近的 token。</summary>
public sealed record PageLintFinding(
    string Prop,
    string Value,
    int Count,
    IReadOnlyList<string> Examples,
    string? NearestToken,
    string? NearestValue,
    double? Distance,
    bool NearMiss);

/// <summary>
/// page lint(<c>parity lint --page</c>,2.5.0):頁面**實際用到的值**有沒有落在 design token 裡。設計 lint 管的是稿,這裡管的是做出來的頁面——
/// 設計稿用對了 token,實作照樣可能手寫一個相近的色碼。不需要配對:頁面上每個看得見的元素都看。
///
/// 看的屬性:文字顏色(有自己的字)、背景(不透明)、邊框色(有邊框)、字級(有自己的字)、內距(每一邊)、圓角。
/// 0、透明、讀不懂的顏色不看。同一個屬性的同一個值只報一次,附元素數與最多 3 個例子——整頁幾千個元素,逐個列會淹沒報告。
/// 排序:near miss 在前(最可能是錯的),同一類裡用得多的在前。
/// </summary>
public static class PageLint
{
    public const int MaxExamples = 3;

    public static IReadOnlyList<PageLintFinding> Run(RenderedNode root, DesignTokens tokens, double colorDeltaE = 2.0)
    {
        var colors = new Dictionary<(string Prop, string Hex), (Rgba Color, List<string> Selectors)>();
        var sizes = new Dictionary<(string Prop, double Px), List<string>>();

        void Color(string prop, Rgba? c, RenderedNode n, string field)
        {
            if (c is not { } color || color.IsTransparent || n.HasUnreadColor(field) || tokens.Colors.Count == 0) return;
            var key = (prop, color.ToHex());
            if (!colors.TryGetValue(key, out var entry)) colors[key] = entry = (color, []);
            entry.Selectors.Add(n.Selector);
        }

        void Size(string prop, double? px, RenderedNode n)
        {
            if (px is not { } v || v <= 0 || tokens.Sizes.Count == 0) return;
            var key = (prop, Math.Round(v, 2));
            if (!sizes.TryGetValue(key, out var list)) sizes[key] = list = [];
            if (list.Count == 0 || list[^1] != n.Selector) list.Add(n.Selector);
        }

        foreach (var n in root.DescendantsAndSelf().Skip(1)) // 根(body)的背景是整頁底色,一樣看——但 body 本身不是元件,跳過它的字級與內距
        {
            var ownText = !string.IsNullOrWhiteSpace(n.Text);
            if (ownText) Color("color", n.Color, n, "color");
            Color("background", n.Background, n, "background");
            if (n.BorderWidth is { } bw && Math.Max(Math.Max(bw.Top, bw.Right), Math.Max(bw.Bottom, bw.Left)) > 0)
                Color("borderColor", n.BorderColor, n, "borderColor");
            if (ownText) Size("fontSize", n.Typography?.FontSize, n);
            foreach (var side in new[] { n.Padding.Top, n.Padding.Right, n.Padding.Bottom, n.Padding.Left }.Distinct())
                Size("padding", side, n);
            Size("cornerRadius", n.CornerRadius, n);
        }
        Color("background", root.Background, root, "background");

        var findings = new List<PageLintFinding>();
        foreach (var ((prop, hex), (color, selectors)) in colors)
        {
            // 原樣命中某個 token(含半透明的 token,例如 rgba 的分隔線色)就過
            if (tokens.Colors.Any(t => Math.Abs(t.Color.A - color.A) <= 0.02 && ColorDelta.Ciede2000(color, t.Color) <= colorDeltaE)) continue;
            // 半透明的值:token 色降了透明度(rgba(var(--ink), .14))算用了那個 token。ΔE 會先合成到白底,
            // 14% 的墨色就變成「像紙色」——所以比的是不透明的那個顏色
            var probe = color.A < 1 ? color with { A = 1.0 } : color;
            var (name, value, d) = tokens.Colors.Select(t => (t.Name, t.Color, D: ColorDelta.Ciede2000(probe, t.Color with { A = 1.0 }))).MinBy(t => t.D);
            if (color.A < 1 && d <= colorDeltaE) continue;
            if (d <= colorDeltaE) continue;
            findings.Add(new PageLintFinding(prop, hex, selectors.Count, Examples(selectors), name, value.ToHex(),
                Math.Round(d, 2), TokenDistance.IsNearMiss(true, d, 0)));
        }
        foreach (var ((prop, px), selectors) in sizes)
        {
            var candidates = SizeTokensFor(prop, tokens);
            if (candidates.Count == 0) continue; // token 檔沒定義這一類(例如沒有任何圓角 token):這一類不檢查
            var (name, value, d) = candidates.Select(t => (t.Name, t.Px, D: Math.Abs(px - t.Px))).MinBy(t => t.D);
            if (d <= 0.01) continue; // 命中(與設計 lint 同一個容忍)
            findings.Add(new PageLintFinding(prop, $"{px:0.##}px", selectors.Count, Examples(selectors), name, $"{value:0.##}px",
                Math.Round(d, 2), TokenDistance.IsNearMiss(false, d, px)));
        }
        return findings
            .OrderByDescending(f => f.NearMiss)
            .ThenByDescending(f => f.Count)
            .ThenBy(f => f.Prop, StringComparer.Ordinal)
            .ThenBy(f => f.Value, StringComparer.Ordinal)
            .ToList();
    }

    private static List<string> Examples(List<string> selectors) => selectors.Distinct().Take(MaxExamples).ToList();

    /// <summary>
    /// 尺寸 token 的種類,看名字猜:token 檔是平面的 名稱 → 值,沒有型別。不分的話 18px 的字級會被說成「改用 space-5 = 20px」。
    /// 名字看不出種類的 token 每一類都算候選;某一類一個候選都沒有,就不檢查那一類。
    /// </summary>
    internal static string? KindOf(string tokenName)
    {
        var n = tokenName.ToLowerInvariant();
        if (n.Contains("font") || n.Contains("text") || n.Contains("type")) return "fontSize";
        if (n.Contains("radius") || n.Contains("rounded") || n.Contains("corner")) return "cornerRadius";
        if (n.Contains("space") || n.Contains("spacing") || n.Contains("gap") || n.Contains("pad") || n.Contains("margin")
            || n.Contains("inset") || n.Contains("gutter")) return "padding";
        return null;
    }

    internal static List<(string Name, double Px)> SizeTokensFor(string prop, DesignTokens tokens)
        => tokens.Sizes.Where(t => KindOf(t.Name) is var k && (k is null || k == prop)).ToList();
}
