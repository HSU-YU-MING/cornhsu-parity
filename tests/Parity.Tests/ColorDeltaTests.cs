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
    public void Parses_wide_gamut_exact(string css, int r, int g, int b, double a)
    {
        Assert.True(Rgba.TryParseCss(css, out var c));
        Assert.Equal((byte)r, c.R);
        Assert.Equal((byte)g, c.G);
        Assert.Equal((byte)b, c.B);
        Assert.Equal(a, c.A, 3);
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
    public void Rejects_unparsable_colors(string? css)
        => Assert.False(Rgba.TryParseCss(css, out _));

    [Fact]
    public void Fully_transparent_is_transparent()
    {
        Rgba.TryParseCss("rgba(0, 0, 0, 0)", out var c);
        Assert.True(c.IsTransparent);
    }
}
