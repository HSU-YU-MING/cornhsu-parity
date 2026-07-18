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
        if (head == "oklch")
            return TryParseOklch(inner, out color);
        if (head == "color")
        {
            inner = inner.TrimStart();
            if (inner.StartsWith("srgb ", StringComparison.OrdinalIgnoreCase) ||
                inner.Equals("srgb", StringComparison.OrdinalIgnoreCase))
                return TryParseComponents(inner[4..], srgb01: true, out color);
            if (inner.StartsWith("display-p3", StringComparison.OrdinalIgnoreCase))
                return TryParseDisplayP3(inner[10..], out color);
            return false; // lab / lch / rec2020 等其餘色域暫不支援
        }
        return false;
    }

    /// <summary>
    /// oklch(L C H [/ a]) → sRGB。L 可為 % 或 0–1;C 的 % 以 0.4 為 100%(CSS Color 4);
    /// H 為角度(可帶 deg)。超出 sRGB 色域的結果 clamp 進來(與瀏覽器的 gamut mapping
    /// 不完全等值,但誤差在 ΔE 容差尺度下可忽略)。
    /// </summary>
    private static bool TryParseOklch(string inner, out Rgba color)
    {
        color = default;
        var tok = inner.Replace('/', ' ').Split(' ', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        if (tok.Length is not (3 or 4)) return false;

        if (!Frac(tok[0], percentScale: 1.0, out var l)) return false;
        if (!Frac(tok[1], percentScale: 0.4, out var c)) return false;
        var hTok = tok[2].EndsWith("deg", StringComparison.OrdinalIgnoreCase) ? tok[2][..^3] : tok[2];
        if (!double.TryParse(hTok, CultureInfo.InvariantCulture, out var hDeg)) return false;
        var alpha = 1.0;
        if (tok.Length == 4 && !Frac(tok[3], percentScale: 1.0, out alpha)) return false;

        var hRad = hDeg * Math.PI / 180.0;
        var (r, g, b) = OklabToLinearSrgb(l, c * Math.Cos(hRad), c * Math.Sin(hRad));
        color = FromLinear(r, g, b, alpha);
        return true;

        // "62.8%" → 0.628×scale基準;純數字原樣
        static bool Frac(string t, double percentScale, out double v)
        {
            if (t.EndsWith('%') && double.TryParse(t[..^1], CultureInfo.InvariantCulture, out var p))
            { v = p / 100.0 * percentScale; return true; }
            return double.TryParse(t, CultureInfo.InvariantCulture, out v);
        }
    }

    /// <summary>color(display-p3 r g b [/ a]) → sRGB(分量 0–1 或 %)。超出 sRGB 的 clamp 進來。</summary>
    private static bool TryParseDisplayP3(string inner, out Rgba color)
    {
        color = default;
        var tok = inner.Replace('/', ' ').Split(' ', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        if (tok.Length is not (3 or 4)) return false;

        Span<double> p3 = stackalloc double[3];
        for (var i = 0; i < 3; i++)
        {
            var t = tok[i];
            if (t.EndsWith('%') && double.TryParse(t[..^1], CultureInfo.InvariantCulture, out var pct))
                p3[i] = pct / 100.0;
            else if (double.TryParse(t, CultureInfo.InvariantCulture, out var n))
                p3[i] = n;
            else return false;
        }
        var alpha = 1.0;
        if (tok.Length == 4)
        {
            var t = tok[3];
            if (t.EndsWith('%') && double.TryParse(t[..^1], CultureInfo.InvariantCulture, out var pct))
                alpha = pct / 100.0;
            else if (!double.TryParse(t, CultureInfo.InvariantCulture, out alpha)) return false;
        }

        // display-p3 與 sRGB 用同一條轉移函數:先線性化,再做線性 P3 → 線性 sRGB 矩陣
        var rl = SrgbToLinear(p3[0]);
        var gl = SrgbToLinear(p3[1]);
        var bl = SrgbToLinear(p3[2]);
        var r = 1.2249401762805786 * rl - 0.22494017628057862 * gl;
        var g = -0.04205695470968816 * rl + 1.0420569547096882 * gl;
        var b = -0.019637554590334432 * rl - 0.07863604555063188 * gl + 1.0982735901409553 * bl;
        color = FromLinear(r, g, b, alpha);
        return true;
    }

    /// <summary>OKLab → 線性 sRGB(Björn Ottosson 的參考矩陣,CSS Color 4 同源)。</summary>
    private static (double R, double G, double B) OklabToLinearSrgb(double l, double a, double b)
    {
        var l_ = l + 0.3963377774 * a + 0.2158037573 * b;
        var m_ = l - 0.1055613458 * a - 0.0638541728 * b;
        var s_ = l - 0.0894841775 * a - 1.2914855480 * b;
        var l3 = l_ * l_ * l_;
        var m3 = m_ * m_ * m_;
        var s3 = s_ * s_ * s_;
        return (
            +4.0767416621 * l3 - 3.3077115913 * m3 + 0.2309699292 * s3,
            -1.2684380046 * l3 + 2.6097574011 * m3 - 0.3413193965 * s3,
            -0.0041960863 * l3 - 0.7034186147 * m3 + 1.7076147010 * s3);
    }

    private static double SrgbToLinear(double c)
        => c <= 0.04045 ? c / 12.92 : Math.Pow((c + 0.055) / 1.055, 2.4);

    private static double LinearToSrgb(double c)
        => c <= 0.0031308 ? 12.92 * c : 1.055 * Math.Pow(c, 1.0 / 2.4) - 0.055;

    private static Rgba FromLinear(double r, double g, double b, double a) => new(
        ClampByte(LinearToSrgb(Math.Clamp(r, 0, 1)) * 255.0),
        ClampByte(LinearToSrgb(Math.Clamp(g, 0, 1)) * 255.0),
        ClampByte(LinearToSrgb(Math.Clamp(b, 0, 1)) * 255.0),
        Math.Clamp(a, 0, 1));

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
