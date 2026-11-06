using System.Text.RegularExpressions;

namespace Parity.Engine.Model;

/// <summary>一個陰影(box-shadow、text-shadow、Figma 的 DROP_SHADOW / INNER_SHADOW)。長度單位是 px。</summary>
public sealed record ShadowSpec(double X, double Y, double Blur, double Spread, Rgba Color, bool Inset = false);

/// <summary>漸層的端點色。第一版不比方向與中間的色標;解析不出來的色彩空間是 null(只知道「有漸層」)。</summary>
public sealed record GradientSpec(Rgba? Start = null, Rgba? End = null);

/// <summary>把 getComputedStyle 的字串解析成可以比較的值。純函式。</summary>
public static partial class CssEffects
{
    /// <summary>
    /// 解析 box-shadow / text-shadow 的計算值。null = 沒有這項資訊;空清單 = 沒有陰影。
    /// 看不見的陰影當作沒有:Tailwind 的元素預設就帶 <c>0 0 #0000</c>,算進去的話每個元素都有「陰影」。
    /// </summary>
    public static IReadOnlyList<ShadowSpec>? ParseShadows(string? css)
    {
        if (css is null) return null;
        var result = new List<ShadowSpec>();
        foreach (var layer in SplitLayers(css))
        {
            var lengths = new List<double>();
            Rgba? color = null;
            var inset = false;
            foreach (var token in Tokens(layer))
            {
                if (token.Equals("inset", StringComparison.OrdinalIgnoreCase)) inset = true;
                else if (Comparison.Normalizer.ParseCssPx(token) is { } px && !token.Equals("normal", StringComparison.OrdinalIgnoreCase)) lengths.Add(px);
                else if (Rgba.TryParseCss(token, out var c)) color = c;
            }
            if (lengths.Count < 2 || color is not { } shadowColor) continue;
            var shadow = new ShadowSpec(lengths[0], lengths[1], lengths.ElementAtOrDefault(2), lengths.ElementAtOrDefault(3), shadowColor, inset);
            var visible = !shadowColor.IsTransparent
                && (shadow.X != 0 || shadow.Y != 0 || shadow.Blur != 0 || shadow.Spread != 0);
            if (visible) result.Add(shadow);
        }
        return result;
    }

    /// <summary>解析 background-image 的計算值:第一個漸層的頭尾色。沒有漸層(none、url())回 null。</summary>
    public static GradientSpec? ParseGradient(string? css)
    {
        if (string.IsNullOrEmpty(css)) return null;
        var at = css.IndexOf("gradient(", StringComparison.OrdinalIgnoreCase);
        if (at < 0) return null;

        var start = at + "gradient(".Length;
        var depth = 1;
        var end = start;
        while (end < css.Length && depth > 0)
        {
            if (css[end] == '(') depth++;
            else if (css[end] == ')') depth--;
            end++;
        }
        var colors = new List<Rgba>();
        foreach (Match m in ColorToken().Matches(css[start..end]))
            if (Rgba.TryParseCss(m.Value, out var c)) colors.Add(c);
        return colors.Count >= 2 ? new GradientSpec(colors[0], colors[^1]) : new GradientSpec();
    }

    /// <summary>套用 text-transform,判斷某種轉換對這段文字有沒有視覺上的差別。認不得的值原樣回傳。</summary>
    public static string ApplyTextTransform(string transform, string text) => transform.ToLowerInvariant() switch
    {
        "uppercase" => text.ToUpperInvariant(),
        "lowercase" => text.ToLowerInvariant(),
        "capitalize" => Capitalize(text),
        _ => text,
    };

    private static string Capitalize(string text)
    {
        var chars = text.ToCharArray();
        var start = true;
        for (var i = 0; i < chars.Length; i++)
        {
            if (start && char.IsLetter(chars[i])) chars[i] = char.ToUpperInvariant(chars[i]);
            start = char.IsWhiteSpace(chars[i]);
        }
        return new string(chars);
    }

    /// <summary>以最外層的逗號切開(rgb(0, 0, 0) 裡的逗號不算)。"none" 沒有任何一層。</summary>
    private static IEnumerable<string> SplitLayers(string css)
    {
        var depth = 0;
        var from = 0;
        for (var i = 0; i <= css.Length; i++)
        {
            if (i < css.Length && css[i] == '(') depth++;
            else if (i < css.Length && css[i] == ')') depth--;
            else if (i == css.Length || (css[i] == ',' && depth == 0))
            {
                var layer = css[from..i].Trim();
                if (layer.Length > 0 && !layer.Equals("none", StringComparison.OrdinalIgnoreCase)) yield return layer;
                from = i + 1;
            }
        }
    }

    /// <summary>以括號外的空白切開。</summary>
    private static IEnumerable<string> Tokens(string layer)
    {
        var depth = 0;
        var from = 0;
        for (var i = 0; i <= layer.Length; i++)
        {
            if (i < layer.Length && layer[i] == '(') depth++;
            else if (i < layer.Length && layer[i] == ')') depth--;
            else if (i == layer.Length || (layer[i] == ' ' && depth == 0))
            {
                if (i > from) yield return layer[from..i];
                from = i + 1;
            }
        }
    }

    [GeneratedRegex(@"(?:rgba?|oklch|color)\([^()]*\)|#[0-9a-fA-F]{3,8}\b", RegexOptions.IgnoreCase)]
    private static partial Regex ColorToken();
}
