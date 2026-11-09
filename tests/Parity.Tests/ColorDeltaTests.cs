using Parity.Engine.Comparison;
using Parity.Engine.Model;

namespace Parity.Tests;

public class ColorDeltaTests
{
    // CIEDE2000 標準測資(Sharma, Wu & Dalal 2005 資料集)——公式抄錯馬上現形
    [Theory]
    [InlineData(50.0, 2.6772, -79.7751, 50.0, 0.0, -82.7485, 2.0425)]
    [InlineData(50.0, 3.1571, -77.2803, 50.0, 0.0, -82.7485, 2.8615)]
    [InlineData(50.0, 2.8361, -74.0200, 50.0, 0.0, -82.7485, 3.4412)]
    [InlineData(50.0, -1.3802, -84.2814, 50.0, 0.0, -82.7485, 1.0000)]
    [InlineData(50.0, 2.5000, 0.0000, 50.0, 0.0, -2.5000, 4.3065)]
    [InlineData(50.0, 2.5000, 0.0000, 61.0, -5.0000, 29.0000, 22.8977)]
    public void Ciede2000_matches_reference_dataset(
        double l1, double a1, double b1, double l2, double a2, double b2, double expected)
    {
        var actual = ColorDelta.Ciede2000((l1, a1, b1), (l2, a2, b2));
        Assert.Equal(expected, actual, 4); // 小數 4 位
    }

    [Fact]
    public void Identical_colors_have_zero_delta()
    {
        var c = new Rgba(0x25, 0x63, 0xEB);
        Assert.Equal(0, ColorDelta.Ciede2000(c, c), 6);
    }

    [Fact]
    public void Similar_blues_are_within_small_delta()
    {
        // 規畫書範例:#2563EB vs #3B82F6 應該是個明顯但不誇張的落差
        Rgba.TryParseCss("#2563EB", out var design);
        Rgba.TryParseCss("#3B82F6", out var actual);
        var deltaE = ColorDelta.Ciede2000(design, actual);
        Assert.InRange(deltaE, 2.0, 15.0);
    }

    [Fact]
    public void Alpha_difference_produces_nonzero_delta()
    {
        // 只差 alpha 的兩色不該算成 0(半透明先合成到白底)
        var opaqueBlack = new Rgba(0, 0, 0, 1.0);
        var halfBlack = new Rgba(0, 0, 0, 0.5); // 合成到白 → 灰
        Assert.True(ColorDelta.Ciede2000(opaqueBlack, halfBlack) > 1.0);
        // 不透明相同色仍為 0(向後相容)
        Assert.Equal(0, ColorDelta.Ciede2000(opaqueBlack, opaqueBlack), 6);
    }

    [Fact]
    public void White_lab_is_100_0_0()
    {
        var (l, a, b) = ColorDelta.ToLab(new Rgba(255, 255, 255));
        Assert.Equal(100, l, 1);
        Assert.Equal(0, a, 1);
        Assert.Equal(0, b, 1);
    }
}

public class RgbaParseTests
{
    [Theory]
    [InlineData("#2563EB", 0x25, 0x63, 0xEB, 1.0)]
    [InlineData("#fff", 255, 255, 255, 1.0)]
    [InlineData("rgb(37, 99, 235)", 37, 99, 235, 1.0)]
    [InlineData("rgba(37, 99, 235, 0.5)", 37, 99, 235, 0.5)]
    [InlineData("rgba(0, 0, 0, 0)", 0, 0, 0, 0.0)]
    // 現代 CSS Color 4 / 新版 Chrome computed style
    [InlineData("rgb(37 99 235)", 37, 99, 235, 1.0)]
    [InlineData("rgb(37 99 235 / 0.5)", 37, 99, 235, 0.5)]
    [InlineData("rgba(37 99 235 / 50%)", 37, 99, 235, 0.5)]
    [InlineData("color(srgb 0.145 0.388 0.922)", 37, 99, 235, 1.0)]
    [InlineData("#2563EB80", 0x25, 0x63, 0xEB, 0.502)]  // 8 位 hex 的 alpha
    [InlineData("rgb(100%, 0%, 50%)", 255, 0, 128, 1.0)] // 百分比分量
    [InlineData("rgb(300, -5, 0)", 255, 0, 0, 1.0)]      // 超界夾回 0–255
    public void Parses_css_colors(string css, int r, int g, int b, double a)
    {
        Assert.True(Rgba.TryParseCss(css, out var c));
        Assert.Equal((byte)r, c.R);
        Assert.Equal((byte)g, c.G);
        Assert.Equal((byte)b, c.B);
        Assert.Equal(a, c.A, 3);
    }

    // oklch / display-p3(CSS Color 4):端點色精確、色域內色允許 ±2/頻道的轉換捨入
    [Theory]
    [InlineData("oklch(1 0 0)", 255, 255, 255, 1.0)]
    [InlineData("oklch(0 0 0)", 0, 0, 0, 1.0)]
    [InlineData("oklch(100% 0 0)", 255, 255, 255, 1.0)]
    [InlineData("color(display-p3 1 1 1)", 255, 255, 255, 1.0)]
    [InlineData("color(display-p3 1 0 0)", 255, 0, 0, 1.0)]           // 超出 sRGB → clamp 到紅
    [InlineData("color(display-p3 0 1 0 / 50%)", 0, 255, 0, 0.5)]
    [InlineData("color(display-p3 100% 100% 100%)", 255, 255, 255, 1.0)] // 分量也可以寫百分比(語法接受)
    [InlineData("color(display-p3 0% 0% 0% / 0.25)", 0, 0, 0, 0.25)]
    public void Parses_wide_gamut_exact(string css, int r, int g, int b, double a)
    {
        Assert.True(Rgba.TryParseCss(css, out var c));
        Assert.Equal((byte)r, c.R);
        Assert.Equal((byte)g, c.G);
        Assert.Equal((byte)b, c.B);
        Assert.Equal(a, c.A, 3);
    }

    // 0% / 100% 落在會被 clamp 的端點,換算比例寫錯(例如 50% 當成 50)也照樣綠——
    // 換算要用端點以外的值,才看得出「50% = 0.5」
    [Theory]
    [InlineData("color(display-p3 50% 25% 75%)", "color(display-p3 0.5 0.25 0.75)", "#893BC6")]
    [InlineData("color(display-p3 50% 0.25 75% / 50%)", "color(display-p3 0.5 0.25 0.75 / 0.5)", "#893BC680")] // 混寫
    public void Display_p3_percentages_scale_like_fractions(string percent, string fraction, string hex)
    {
        Assert.True(Rgba.TryParseCss(percent, out var fromPercent));
        Assert.True(Rgba.TryParseCss(fraction, out var fromFraction));
        Assert.Equal(fromFraction, fromPercent);
        Assert.Equal(hex, fromPercent.ToHex()); // CSS Color 4:線性化 → P3→sRGB 矩陣 → 編碼
    }

    [Theory]
    [InlineData("oklch(0.6279 0.2577 29.23)", 255, 0, 0)]        // sRGB 紅的 oklch 座標
    [InlineData("oklch(62.79% 0.2577 29.23deg)", 255, 0, 0)]     // % 與 deg 寫法
    [InlineData("oklch(0.452 0.3132 264.05)", 0, 0, 255)]        // sRGB 藍的 oklch 座標
    [InlineData("color(display-p3 0.5 0.5 0.5)", 128, 128, 128)] // 同轉移函數 → 灰幾乎不變(±1 捨入)
    public void Parses_oklch_within_rounding(string css, int r, int g, int b)
    {
        Assert.True(Rgba.TryParseCss(css, out var c));
        Assert.InRange(c.R, Math.Max(0, r - 2), Math.Min(255, r + 2));
        Assert.InRange(c.G, Math.Max(0, g - 2), Math.Min(255, g + 2));
        Assert.InRange(c.B, Math.Max(0, b - 2), Math.Min(255, b + 2));
    }

    [Fact]
    public void Oklch_alpha_parses()
    {
        Assert.True(Rgba.TryParseCss("oklch(1 0 0 / 0.5)", out var c));
        Assert.Equal(0.5, c.A, 3);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("transparent")]
    [InlineData("var(--color)")]
    [InlineData("color(rec2020 1 0 0)")]
    [InlineData("oklch(banana 0 0)")]
    [InlineData("#12345")]                       // hex 長度不對
    [InlineData("#ggg")]                         // 不是 hex
    [InlineData("rgb(1, 2)")]                    // 分量不足
    [InlineData("rgb(a, b, c)")]
    [InlineData("rgba(1, 2, 3, x)")]             // alpha 壞掉
    [InlineData("oklch(0.5 0.1)")]
    [InlineData("color(display-p3 1 x 0)")]
    [InlineData("color(display-p3 1 0 0 / x)")]
    [InlineData("rgb 1 2 3")]                    // 沒有括號
    [InlineData("color(a98-rgb 0.4 0.5 0.6)")]   // 還不支援的色域:擷取端會記成「讀不懂」,不比、明講
    [InlineData("color(prophoto-rgb 0.5 0.4 0.3)")]
    [InlineData("oklab(0.5 0.1)")]               // 分量不足
    [InlineData("lab(50 x 0)")]
    [InlineData("lch(50 30 banana)")]
    public void Rejects_unparsable_colors(string? css)
        => Assert.False(Rgba.TryParseCss(css, out _));

    // oklab / lab / lch / xyz / srgb-linear(2.5.0)。期望值是 Chromium 156 自己換算的結果:
    // getComputedStyle(color-mix(in srgb, X 100%, black 0%)) 得到 color(srgb …),再乘 255 四捨五入、超出色域的夾到 0–255。
    // 字串都是 Chromium 計算值實際長的樣子(lab 的 L 是 0–100 的數字、oklab 的 L 是 0–1)。允許 ±1/頻道的捨入
    [Theory]
    [InlineData("oklab(0.6 0.1 -0.1)", 159, 99, 186)]
    [InlineData("oklab(0.4 0.05 0.02)", 98, 60, 59)]
    [InlineData("oklab(0.623 -0.0378409 -0.210628)", 43, 127, 255)] // Tailwind 的 blue-500 混一半透明(超出 sRGB → 夾)
    [InlineData("lab(50 40 -20)", 171, 90, 154)]
    [InlineData("lab(30 10 10)", 89, 65, 56)]
    [InlineData("lab(90 -20 60)", 216, 236, 104)]
    [InlineData("lch(60 50 120)", 118, 156, 62)]
    [InlineData("lch(20 30 270)", 8, 50, 93)]
    [InlineData("lch(75 40 30)", 252, 159, 150)]
    [InlineData("oklch(0.7 none 120)", 158, 158, 158)]                 // none = 0(Chromium 原樣回 none)
    [InlineData("color(xyz-d65 0.2 0.3 0.4)", 0, 167, 164)]
    [InlineData("color(xyz 0.2 0.3 0.4)", 0, 167, 164)]               // xyz = xyz-d65
    [InlineData("color(xyz-d50 0.2 0.3 0.4)", 0, 168, 189)]
    [InlineData("color(srgb-linear 0.2 0.5 0.8)", 124, 188, 231)]
    public void Parses_lab_family_like_chromium(string css, int r, int g, int b)
    {
        Assert.True(Rgba.TryParseCss(css, out var c));
        Assert.InRange(c.R, Math.Max(0, r - 1), Math.Min(255, r + 1));
        Assert.InRange(c.G, Math.Max(0, g - 1), Math.Min(255, g + 1));
        Assert.InRange(c.B, Math.Max(0, b - 1), Math.Min(255, b + 1));
        Assert.Equal(1.0, c.A, 3);
    }

    [Theory]
    [InlineData("oklab(0.4 0.05 0.02 / 0.5)", 0.5)]   // Chromium 的半透明寫法
    [InlineData("lab(30 10 10 / 0.5)", 0.5)]
    [InlineData("lab(30 10 10 / 50%)", 0.5)]
    [InlineData("lch(20 30 270 / 0.25)", 0.25)]
    [InlineData("color(xyz-d65 0.2 0.3 0.4 / 0.75)", 0.75)]
    public void Lab_family_alpha(string css, double alpha)
    {
        Assert.True(Rgba.TryParseCss(css, out var c));
        Assert.Equal(alpha, c.A, 3);
    }

    [Theory]
    [InlineData("lab(50% 40 -20)", "lab(50 40 -20)")]          // L 的 % 以 100 為 100%
    [InlineData("lab(50 32% -16%)", "lab(50 40 -20)")]         // a、b 的 % 以 125 為 100%
    [InlineData("lch(60 33.3333% 120deg)", "lch(60 50 120)")]  // C 的 % 以 150 為 100%,色相可帶 deg
    [InlineData("oklab(60% 25% -25%)", "oklab(0.6 0.1 -0.1)")] // a、b 的 % 以 0.4 為 100%
    public void Lab_family_percentages(string percent, string plain)
    {
        Assert.True(Rgba.TryParseCss(percent, out var a));
        Assert.True(Rgba.TryParseCss(plain, out var b));
        Assert.Equal(b, a);
    }

    [Fact]
    public void Hex_carries_alpha_only_when_translucent()
    {
        // 報告的 expected/actual 用 ToHex:不透明 6 位、半透明 8 位(README 的 #RRGGBB / #RRGGBBAA)
        Assert.Equal("#2563EB", new Rgba(0x25, 0x63, 0xEB).ToHex());
        Assert.Equal("#2563EB80", new Rgba(0x25, 0x63, 0xEB, 0.5).ToHex());
        Assert.Equal("#2563EB80", new Rgba(0x25, 0x63, 0xEB, 0.5).ToString());
    }

    [Fact]
    public void Fully_transparent_is_transparent()
    {
        Rgba.TryParseCss("rgba(0, 0, 0, 0)", out var c);
        Assert.True(c.IsTransparent);
    }
}
