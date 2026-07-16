using Parity.Engine;
using Parity.Engine.Comparison;
using Parity.Engine.DesignSources;
using Parity.Engine.Model;
using static Parity.Tests.TestData;

namespace Parity.Tests;

public class DiffEngineTests
{
    private static readonly DiffEngine Engine = new(new Tolerances());

    [Fact]
    public void Reports_padding_mismatch_with_exact_numbers()
    {
        // 規畫書第 1 節的招牌案例:「內距 8px,設計 12px」
        var pair = new NodePair(
            Design("d1", "cta-button", box: new Box(0, 0, 160, 48),
                padding: new Insets(12, 20, 12, 20)),
            Rendered("main > button.cta", "button", box: new Box(0, 0, 160, 48),
                padding: new Insets(12, 8, 12, 8)),
            "auto-text");

        var result = Engine.Diff(pair);

        var right = result.Diffs.Single(d => d.Prop == "paddingRight");
        Assert.Equal("20", right.Expected);
        Assert.Equal("8", right.Actual);
        var left = result.Diffs.Single(d => d.Prop == "paddingLeft");
        Assert.Equal(12, left.Delta);
        Assert.DoesNotContain(result.Diffs, d => d.Prop is "paddingTop" or "paddingBottom");
    }

    [Fact]
    public void Within_tolerance_produces_no_diff()
    {
        // 子像素差異必然存在 → 容差內不報(規畫書 4.8)
        var pair = new NodePair(
            Design("d1", "card", box: new Box(0, 0, 160, 48)),
            Rendered("div.card", box: new Box(0, 0, 161.5, 47.2)),
            "auto-name");

        var result = Engine.Diff(pair);

        Assert.Empty(result.Diffs);
        Assert.Equal(Severity.None, result.Severity);
    }

    [Fact]
    public void Absolute_position_is_never_compared()
    {
        // 預設不比絕對位置——擺哪不重要,多大才重要(規畫書 4.8 關鍵決策)
        var pair = new NodePair(
            Design("d1", "card", box: new Box(40, 500, 160, 48)),
            Rendered("div.card", box: new Box(700, 10, 160, 48)),
            "auto-name");

        var result = Engine.Diff(pair);

        Assert.Empty(result.Diffs);
    }

    [Fact]
    public void Text_nodes_skip_size_but_compare_typography_and_color()
    {
        var pair = new NodePair(
            Design("d1", "page-title", DesignNodeType.Text,
                box: new Box(0, 0, 300, 38),
                fill: new Rgba(0x11, 0x18, 0x27),
                text: new Typography("Inter", FontSize: 16, FontWeight: 700)),
            Rendered("h1", "h1", text: "標題",
                box: new Box(0, 0, 784, 38), // 文字框寬度天生不同 → 不比
                color: new Rgba(0x11, 0x18, 0x27),
                typography: new Typography("Inter, sans-serif", FontSize: 15, FontWeight: 700)),
            "auto-text");

        var result = Engine.Diff(pair);

        Assert.DoesNotContain(result.Diffs, d => d.Prop is "width" or "height");
        var fontSize = result.Diffs.Single(d => d.Prop == "fontSize");
        Assert.Equal("16", fontSize.Expected);
        Assert.Equal("15", fontSize.Actual);
        Assert.DoesNotContain(result.Diffs, d => d.Prop == "fontFamily"); // stack 含 Inter → 過
        Assert.DoesNotContain(result.Diffs, d => d.Prop == "color");      // 同色 → 過
    }

    [Fact]
    public void Font_family_mismatch_is_soft()
    {
        // Figma 名 ≠ CSS font stack → 軟落差,不擋 gate(規畫書風險表)
        var pair = new NodePair(
            Design("d1", "title", DesignNodeType.Text,
                text: new Typography("Roboto", FontSize: 16)),
            Rendered("h1", "h1",
                typography: new Typography("Arial, sans-serif", FontSize: 16)),
            "auto-text");

        var result = Engine.Diff(pair);

        var family = result.Diffs.Single(d => d.Prop == "fontFamily");
        Assert.True(family.Soft);
        Assert.Equal(Severity.Minor, family.Severity);
    }

    [Fact]
    public void Color_diff_uses_deltaE()
    {
        var pair = new NodePair(
            Design("d1", "cta", box: new Box(0, 0, 100, 40),
                fill: new Rgba(0x25, 0x63, 0xEB)),
            Rendered("button", "button", box: new Box(0, 0, 100, 40),
                background: new Rgba(0x3B, 0x82, 0xF6)),
            "auto-name");

        var result = Engine.Diff(pair);

        var bg = result.Diffs.Single(d => d.Prop == "background");
        Assert.Equal("#2563EB", bg.Expected);
        Assert.Equal("#3B82F6", bg.Actual);
        Assert.NotNull(bg.Delta); // ΔE 值要進報告
        Assert.True(bg.Delta > 2.0);
    }

    [Fact]
    public void Transparent_background_falls_back_to_effective_background()
    {
        // 元素本身 transparent、祖先有塗色 → 用有效背景比,不誤報
        var effective = new Rgba(0x25, 0x63, 0xEB);
        var pair = new NodePair(
            Design("d1", "banner", box: new Box(0, 0, 100, 40), fill: effective),
            Rendered("div.banner", box: new Box(0, 0, 100, 40),
                background: new Rgba(0, 0, 0, 0)) with
            { EffectiveBackground = effective },
            "auto-name");

        var result = Engine.Diff(pair);

        Assert.DoesNotContain(result.Diffs, d => d.Prop == "background");
    }

    [Fact]
    public void Item_spacing_compares_against_actual_child_gaps()
    {
        var pair = new NodePair(
            Design("d1", "nav", box: new Box(0, 0, 300, 40),
                itemSpacing: 16, layoutMode: "HORIZONTAL",
                children:
                [
                    Design("d2", "a", box: new Box(0, 0, 50, 40)),
                    Design("d3", "b", box: new Box(66, 0, 50, 40)),
                ]),
            Rendered("nav", "nav", box: new Box(0, 0, 300, 40),
                children:
                [
                    Rendered("nav > a:nth-of-type(1)", "a", box: new Box(0, 0, 50, 40)),
                    Rendered("nav > a:nth-of-type(2)", "a", box: new Box(58, 0, 50, 40)), // gap 8, 設計 16
                ]),
            "auto-name");

        var result = Engine.Diff(pair);

        var spacing = result.Diffs.Single(d => d.Prop == "itemSpacing");
        Assert.Equal("16", spacing.Expected);
        Assert.Equal("8", spacing.Actual);
    }

    [Theory]
    [InlineData(4.0, Severity.Medium)]    // 超容差 2 倍
    [InlineData(10.0, Severity.Serious)]  // 5 倍
    [InlineData(30.0, Severity.Critical)] // 15 倍
    public void Severity_scales_with_ratio_over_tolerance(double actualWidth, Severity expected)
    {
        var pair = new NodePair(
            Design("d1", "box", box: new Box(0, 0, 0, 10)),
            Rendered("div", box: new Box(0, 0, actualWidth, 10)),
            "auto-name");

        var result = Engine.Diff(pair);

        var width = result.Diffs.Single(d => d.Prop == "width");
        Assert.Equal(expected, width.Severity);
    }
}
