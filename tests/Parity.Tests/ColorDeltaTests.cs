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
    public void Parses_css_colors(string css, int r, int g, int b, double a)
    {
        Assert.True(Rgba.TryParseCss(css, out var c));
        Assert.Equal((byte)r, c.R);
        Assert.Equal((byte)g, c.G);
        Assert.Equal((byte)b, c.B);
        Assert.Equal(a, c.A, 3);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("transparent")]
    [InlineData("var(--color)")]
    public void Rejects_unparsable_colors(string? css)
        => Assert.False(Rgba.TryParseCss(css, out _));

    [Fact]
    public void Fully_transparent_is_transparent()
    {
        Rgba.TryParseCss("rgba(0, 0, 0, 0)", out var c);
        Assert.True(c.IsTransparent);
    }
}
