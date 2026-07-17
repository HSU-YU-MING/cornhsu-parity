using System.Globalization;

namespace Parity.Engine.Model;

/// <summary>sRGB 顏色。統一格式:0–255 分量 + 0–1 alpha(規畫書 4.6:顏色統一轉 sRGB)。</summary>
public readonly record struct Rgba(byte R, byte G, byte B, double A = 1.0)
{
    public string ToHex() => A >= 1.0
        ? $"#{R:X2}{G:X2}{B:X2}"
        : $"#{R:X2}{G:X2}{B:X2}{(byte)Math.Round(A * 255):X2}";

    public bool IsTransparent => A <= 0.001;

    public override string ToString() => ToHex();

    /// <summary>解析 CSS 顏色字串:#rgb/#rrggbb/#rrggbbaa、rgb()、rgba()。</summary>
    public static bool TryParseCss(string? s, out Rgba color)
    {
        color = default;
        if (string.IsNullOrWhiteSpace(s)) return false;
        s = s.Trim();

        if (s.StartsWith('#'))
        {
            var hex = s[1..];
            if (hex.Length == 3)
                hex = $"{hex[0]}{hex[0]}{hex[1]}{hex[1]}{hex[2]}{hex[2]}";
            if (hex.Length is not (6 or 8)) return false;
            if (!uint.TryParse(hex, NumberStyles.HexNumber, CultureInfo.InvariantCulture, out _)) return false;
            byte P(int i) => byte.Parse(hex.AsSpan(i, 2), NumberStyles.HexNumber, CultureInfo.InvariantCulture);
            var a = hex.Length == 8 ? P(6) / 255.0 : 1.0;
            color = new Rgba(P(0), P(2), P(4), a);
            return true;
        }

        // 函式式:rgb()/rgba()、以及 color(srgb …)。分隔支援逗號或空白、斜線 alpha、百分比
        // (新版 Chrome computed style 可能回 "rgb(37 99 235 / .5)" 或 "color(srgb …)")。
        var open = s.IndexOf('(');
        var close = s.LastIndexOf(')');
        if (open < 0 || close <= open) return false;
        var head = s[..open].Trim().ToLowerInvariant();
        var inner = s[(open + 1)..close];

        if (head is "rgb" or "rgba")
            return TryParseComponents(inner, srgb01: false, out color);
        if (head == "color")
        {
            inner = inner.TrimStart();
            if (inner.StartsWith("srgb", StringComparison.OrdinalIgnoreCase))
                return TryParseComponents(inner[4..], srgb01: true, out color);
            return false; // display-p3 / oklch / lab 等色域暫不支援
        }
        return false;
    }

    /// <summary>解析 "r g b [/ a]"(逗號/空白/斜線分隔;% 允許)。srgb01=true 時分量是 0–1。</summary>
    private static bool TryParseComponents(string inner, bool srgb01, out Rgba color)
    {
        color = default;
        var tok = inner.Replace(',', ' ').Replace('/', ' ')
            .Split(' ', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        if (tok.Length is not (3 or 4)) return false;

        var scale = srgb01 ? 255.0 : 1.0;
        if (!Comp(tok[0], scale, out var r) || !Comp(tok[1], scale, out var g) || !Comp(tok[2], scale, out var b))
            return false;
        var a = 1.0;
        if (tok.Length == 4 && !Alpha(tok[3], out a)) return false;

        color = new Rgba(ClampByte(r), ClampByte(g), ClampByte(b), Math.Clamp(a, 0, 1));
        return true;

        static bool Comp(string t, double scale, out double v)
        {
            if (t.EndsWith('%') && double.TryParse(t[..^1], CultureInfo.InvariantCulture, out var p))
            { v = p / 100.0 * 255.0; return true; }
            var ok = double.TryParse(t, CultureInfo.InvariantCulture, out var n);
            v = n * scale; return ok;
        }
        static bool Alpha(string t, out double a)
        {
            if (t.EndsWith('%') && double.TryParse(t[..^1], CultureInfo.InvariantCulture, out var p))
            { a = p / 100.0; return true; }
            return double.TryParse(t, CultureInfo.InvariantCulture, out a);
        }
    }

    private static byte ClampByte(double v) => (byte)Math.Clamp(Math.Round(v), 0, 255);
}
