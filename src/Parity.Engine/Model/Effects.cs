using System.Globalization;
using System.Text.Json.Serialization;
using System.Text.RegularExpressions;

namespace Parity.Engine.Model;

/// <summary>一個陰影(box-shadow、text-shadow、Figma 的 DROP_SHADOW / INNER_SHADOW)。長度單位是 px。</summary>
public sealed record ShadowSpec(double X, double Y, double Blur, double Spread, Rgba Color, bool Inset = false);

/// <summary>漸層的端點色、種類與方向。不比中間的色標;解析不出來的色彩空間是 null(只知道「有漸層」)。</summary>
public sealed record GradientSpec(Rgba? Start = null, Rgba? End = null)
{
    /// <summary>"linear" / "radial" / "conic"。null = 不知道(Figma 的 DIAMOND 在 CSS 沒有對應)。</summary>
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? Kind { get; init; }

    /// <summary>線性漸層的方向,用 CSS 的角度:0 = 往上、90 = 往右。null = 不是線性,或是要看框的比例才知道的(to top right)。</summary>
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public double? Angle { get; init; }
}

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

    /// <summary>解析 background-image 的計算值:第一個漸層的種類、方向與頭尾色。沒有漸層(none、url())回 null。</summary>
    public static GradientSpec? ParseGradient(string? css)
    {
        if (string.IsNullOrEmpty(css)) return null;
        var head = GradientHead().Match(css);
        if (!head.Success) return null;

        var kind = head.Groups[1].Value.ToLowerInvariant();
        var start = head.Index + head.Length;
        var depth = 1;
        var end = start;
        while (end < css.Length && depth > 0)
        {
            if (css[end] == '(') depth++;
            else if (css[end] == ')') depth--;
            end++;
        }
        var body = css[start..Math.Max(start, end - 1)];
        var colors = new List<Rgba>();
        foreach (Match m in ColorToken().Matches(body))
            if (Rgba.TryParseCss(m.Value, out var c)) colors.Add(c);
        var angle = kind == "linear" ? LinearAngle(body) : null;
        return (colors.Count >= 2 ? new GradientSpec(colors[0], colors[^1]) : new GradientSpec()) with { Kind = kind, Angle = angle };
    }

    /// <summary>線性漸層的方向:第一個參數是角度或 to 關鍵字;直接就是顏色的話預設往下(180)。對角的 to 要看框的比例,回 null。</summary>
    private static double? LinearAngle(string body)
    {
        var comma = 0;
        var depth = 0;
        while (comma < body.Length && (body[comma] != ',' || depth > 0))
        {
            if (body[comma] == '(') depth++;
            else if (body[comma] == ')') depth--;
            comma++;
        }
        var first = body[..comma].Trim().ToLowerInvariant();

        var number = AngleToken().Match(first);
        if (number.Success && double.TryParse(number.Groups[1].Value, CultureInfo.InvariantCulture, out var value))
        {
            var degrees = number.Groups[2].Value switch
            {
                "turn" => value * 360,
                "rad" => value * 180 / Math.PI,
                "grad" => value * 0.9,
                _ => value,
            };
            return (degrees % 360 + 360) % 360;
        }
        return first switch
        {
            "to top" => 0,
            "to right" => 90,
            "to bottom" => 180,
            "to left" => 270,
            _ when first.StartsWith("to ", StringComparison.Ordinal) => null,
            _ => 180,
        };
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

    [GeneratedRegex(@"(?:repeating-)?(linear|radial|conic)-gradient\(", RegexOptions.IgnoreCase)]
    private static partial Regex GradientHead();

    [GeneratedRegex(@"^(-?[0-9.]+)(deg|turn|rad|grad)$")]
    private static partial Regex AngleToken();

    [GeneratedRegex(@"(?:rgba?|oklch|oklab|lch|lab|color)\([^()]*\)|#[0-9a-fA-F]{3,8}\b", RegexOptions.IgnoreCase)]
    private static partial Regex ColorToken();

    /// <summary>
    /// 計算值裡 <see cref="Rgba.TryParseCss"/> 讀不懂的顏色(例如 <c>color(rec2020 …)</c>),原樣回傳。
    /// 讀不懂的顏色不能當成「沒有」:背景會被報成透明、陰影會被報成不見(2.5.0 以前就是這樣)。擷取端把它記下來,比對跳過、主控台明講。
    /// </summary>
    public static IEnumerable<string> UnreadableColors(string? css)
    {
        if (string.IsNullOrEmpty(css)) yield break;
        foreach (Match m in AnyColorFunction().Matches(css))
            if (!Rgba.TryParseCss(m.Value, out _)) yield return m.Value;
    }

    // 比 ColorToken 寬:任何顏色函式都抓,讀不讀得懂交給 TryParseCss 判斷。hsl()、hwb() 的計算值 Chromium 會轉成 rgb(),寫進來是保險
    [GeneratedRegex(@"\b(?:rgba?|hsla?|hwb|oklch|oklab|lch|lab|color)\([^()]*\)", RegexOptions.IgnoreCase)]
    private static partial Regex AnyColorFunction();
}
