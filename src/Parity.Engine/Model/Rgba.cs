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

    /// <summary>解析 CSS 顏色字串:#rgb/#rrggbb/#rrggbbaa、rgb()、rgba()、oklch()、oklab()、lab()、lch()、color(srgb / display-p3 / srgb-linear / xyz …)。</summary>
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
        // Chromium 的計算值保留原本的色彩空間:Tailwind v4 的半透明色與 color-mix() 都會變成 oklab(… / 0.5)(2.5.0)
        if (head == "oklab")
            return TryParseOklab(inner, out color);
        if (head is "lab" or "lch")
            return TryParseLab(inner, polar: head == "lch", out color);
        if (head == "color")
        {
            inner = inner.TrimStart();
            var space = inner.Split(' ', 2)[0].ToLowerInvariant();
            var rest = inner[space.Length..];
            return space switch
            {
                "srgb" => TryParseComponents(rest, srgb01: true, out color),
                "display-p3" => TryParseDisplayP3(rest, out color),
                "srgb-linear" => TryParseLinear(rest, LinearSrgbFrom.Identity, out color),
                "xyz" or "xyz-d65" => TryParseLinear(rest, LinearSrgbFrom.XyzD65, out color),
                "xyz-d50" => TryParseLinear(rest, LinearSrgbFrom.XyzD50, out color),
                _ => false, // rec2020、a98-rgb、prophoto-rgb 暫不支援:擷取端會記下來,不比、明講(UnreadColor)
            };
        }
        return false;
    }

    /// <summary>
    /// 函式式顏色的分量:空白或斜線分隔,3 個加選填的 alpha。<c>none</c>(CSS Color 4:這個分量沒有值)算 0,
    /// 跟瀏覽器畫出來的一樣。
    /// </summary>
    private static string[]? Tokens(string inner)
    {
        var tok = inner.Replace('/', ' ').Split(' ', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        return tok.Length is 3 or 4 ? tok : null;
    }

    /// <summary>"62.8%" → 0.628 × percentScale;純數字原樣;none → 0。</summary>
    private static bool Number(string t, double percentScale, out double v)
    {
        if (t.Equals("none", StringComparison.OrdinalIgnoreCase)) { v = 0; return true; }
        if (t.EndsWith('%') && double.TryParse(t[..^1], CultureInfo.InvariantCulture, out var p))
        { v = p / 100.0 * percentScale; return true; }
        return double.TryParse(t, CultureInfo.InvariantCulture, out v);
    }

    /// <summary>色相:角度,可帶 deg;none → 0。</summary>
    private static bool Hue(string t, out double degrees)
        => Number(t.EndsWith("deg", StringComparison.OrdinalIgnoreCase) ? t[..^3] : t, percentScale: 1, out degrees);

    private static bool Alpha(string[] tok, out double alpha)
    {
        alpha = 1.0;
        return tok.Length == 3 || Number(tok[3], percentScale: 1.0, out alpha);
    }

    /// <summary>oklab(L a b [/ alpha]):L 是 0–1 或 %;a、b 的 % 以 0.4 為 100%(CSS Color 4)。</summary>
    private static bool TryParseOklab(string inner, out Rgba color)
    {
        color = default;
        if (Tokens(inner) is not { } tok) return false;
        if (!Number(tok[0], 1.0, out var l) || !Number(tok[1], 0.4, out var a) || !Number(tok[2], 0.4, out var b) || !Alpha(tok, out var alpha))
            return false;
        var (r, g, bl) = OklabToLinearSrgb(l, a, b);
        color = FromLinear(r, g, bl, alpha);
        return true;
    }

    /// <summary>
    /// lab(L a b) 與 lch(L C H):CIE Lab,D50 白點(CSS Color 4)。L 是 0–100(% 以 100 為 100%);
    /// a、b 的 % 以 125 為 100%,C 的 % 以 150 為 100%。轉成 XYZ D50 → Bradford 轉 D65 → 線性 sRGB。
    /// </summary>
    private static bool TryParseLab(string inner, bool polar, out Rgba color)
    {
        color = default;
        if (Tokens(inner) is not { } tok) return false;
        if (!Number(tok[0], 100.0, out var l) || !Alpha(tok, out var alpha)) return false;
        double a, b;
        if (polar)
        {
            if (!Number(tok[1], 150.0, out var c) || !Hue(tok[2], out var h)) return false;
            a = c * Math.Cos(h * Math.PI / 180.0);
            b = c * Math.Sin(h * Math.PI / 180.0);
        }
        else if (!Number(tok[1], 125.0, out a) || !Number(tok[2], 125.0, out b)) return false;

        const double kappa = 24389.0 / 27, epsilon = 216.0 / 24389;
        var f1 = (l + 16) / 116;
        var f0 = a / 500 + f1;
        var f2 = f1 - b / 200;
        var x = Math.Pow(f0, 3) > epsilon ? Math.Pow(f0, 3) : (116 * f0 - 16) / kappa;
        var y = l > kappa * epsilon ? Math.Pow(f1, 3) : l / kappa;
        var z = Math.Pow(f2, 3) > epsilon ? Math.Pow(f2, 3) : (116 * f2 - 16) / kappa;
        var (r, g, bl) = LinearSrgbFrom.XyzD50(x * D50White[0], y * D50White[1], z * D50White[2]);
        color = FromLinear(r, g, bl, alpha);
        return true;
    }

    /// <summary>D50 白點的 XYZ(CSS Color 4 用 x = 0.3457、y = 0.3585 算的)。</summary>
    private static readonly double[] D50White = [0.3457 / 0.3585, 1.0, (1.0 - 0.3457 - 0.3585) / 0.3585];

    /// <summary>color(srgb-linear …)、color(xyz …) 這類:三個分量線性轉成線性 sRGB。</summary>
    private static bool TryParseLinear(string inner, Func<double, double, double, (double, double, double)> toLinearSrgb, out Rgba color)
    {
        color = default;
        if (Tokens(inner) is not { } tok) return false;
        if (!Number(tok[0], 1.0, out var c0) || !Number(tok[1], 1.0, out var c1) || !Number(tok[2], 1.0, out var c2) || !Alpha(tok, out var alpha))
            return false;
        var (r, g, b) = toLinearSrgb(c0, c1, c2);
        color = FromLinear(r, g, b, alpha);
        return true;
    }

    /// <summary>轉到線性 sRGB 的矩陣(CSS Color 4 的參考實作)。</summary>
    private static class LinearSrgbFrom
    {
        public static (double, double, double) Identity(double r, double g, double b) => (r, g, b);

        public static (double, double, double) XyzD65(double x, double y, double z) => (
            3.2409699419045226 * x - 1.537383177570094 * y - 0.4986107602930034 * z,
            -0.9692436362808796 * x + 1.8759675015077202 * y + 0.04155505740717559 * z,
            0.05563007969699366 * x - 0.20397695888897652 * y + 1.0569715142428786 * z);

        // Bradford 色適應:D50 → D65,再轉線性 sRGB
        public static (double, double, double) XyzD50(double x, double y, double z) => XyzD65(
            0.955473421488075 * x - 0.02309845494876471 * y + 0.06325924320057072 * z,
            -0.0283697093338637 * x + 1.0099953980813041 * y + 0.021041441191917323 * z,
            0.012314014864481998 * x - 0.020507649298898964 * y + 1.330365926242124 * z);
    }

    /// <summary>
    /// oklch(L C H [/ a]) → sRGB。L 可為 % 或 0–1;C 的 % 以 0.4 為 100%(CSS Color 4);
    /// H 為角度(可帶 deg)。超出 sRGB 色域的結果 clamp 進來(與瀏覽器的 gamut mapping
    /// 不完全等值,但誤差在 ΔE 容差尺度下可忽略)。
    /// </summary>
    private static bool TryParseOklch(string inner, out Rgba color)
    {
        color = default;
        if (Tokens(inner) is not { } tok) return false;
        if (!Number(tok[0], 1.0, out var l) || !Number(tok[1], 0.4, out var c) || !Hue(tok[2], out var hDeg) || !Alpha(tok, out var alpha))
            return false;

        var hRad = hDeg * Math.PI / 180.0;
        var (r, g, b) = OklabToLinearSrgb(l, c * Math.Cos(hRad), c * Math.Sin(hRad));
        color = FromLinear(r, g, b, alpha);
        return true;
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
