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

        if (s.StartsWith("rgb", StringComparison.OrdinalIgnoreCase))
        {
            var open = s.IndexOf('(');
            var close = s.IndexOf(')');
            if (open < 0 || close <= open) return false;
            var parts = s[(open + 1)..close].Split(',', StringSplitOptions.TrimEntries);
            if (parts.Length is not (3 or 4)) return false;
            if (!double.TryParse(parts[0], CultureInfo.InvariantCulture, out var r) ||
                !double.TryParse(parts[1], CultureInfo.InvariantCulture, out var g) ||
                !double.TryParse(parts[2], CultureInfo.InvariantCulture, out var b)) return false;
            var a2 = 1.0;
            if (parts.Length == 4 && !double.TryParse(parts[3], CultureInfo.InvariantCulture, out a2)) return false;
            color = new Rgba(ClampByte(r), ClampByte(g), ClampByte(b), Math.Clamp(a2, 0, 1));
            return true;
        }

        return false;
    }

    private static byte ClampByte(double v) => (byte)Math.Clamp(Math.Round(v), 0, 255);
}
