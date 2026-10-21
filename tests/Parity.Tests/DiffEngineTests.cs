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

    [Fact]
    public void Hug_contents_width_is_not_compared()
    {
        // auto-layout 按鈕隨內容決定寬度:Figma 量的寬 ≠ 瀏覽器渲染寬是必然的,不該誤報
        var pair = new NodePair(
            Design("d1", "cta-button", box: new Box(0, 0, 210, 40), sizingH: "HUG"),
            Rendered("button", "button", box: new Box(0, 0, 235, 40)),
            "auto-text");

        var result = Engine.Diff(pair);

        Assert.DoesNotContain(result.Diffs, d => d.Prop == "width");
    }

    [Fact]
    public void Fill_height_is_not_compared_but_fixed_axis_still_is()
    {
        // 垂直 FILL(隨父層)→ 不比高;水平 FIXED → 照比寬
        var pair = new NodePair(
            Design("d1", "sidebar", box: new Box(0, 0, 240, 600),
                sizingH: "FIXED", sizingV: "FILL"),
            Rendered("aside", box: new Box(0, 0, 200, 900)),
            "auto-name");

        var result = Engine.Diff(pair);

        Assert.DoesNotContain(result.Diffs, d => d.Prop == "height");
        var width = result.Diffs.Single(d => d.Prop == "width");
        Assert.Equal("240", width.Expected);
    }

    [Fact]
    public void Unknown_sizing_still_compares_size()
    {
        // 手寫 JSON / 非 auto-layout 沒有 sizing 欄位 → 維持原本行為(照比),向後相容
        var pair = new NodePair(
            Design("d1", "card", box: new Box(0, 0, 300, 200)),
            Rendered("div.card", box: new Box(0, 0, 340, 200)),
            "auto-name");

        var result = Engine.Diff(pair);

        Assert.Contains(result.Diffs, d => d.Prop == "width");
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

    [Fact]
    public void Missing_background_is_reported_as_status_missing_without_a_delta()
    {
        // 設計有底色、頁面元素與祖先都沒有 → 不是「顏色不同」而是「整個沒做」:status missing,
        // 沒有 ΔE 可算(delta 顯式 null),actual 寫成 (transparent)
        var pair = new NodePair(
            Design("d1", "banner", box: new Box(0, 0, 100, 40), fill: new Rgba(0x25, 0x63, 0xEB)),
            Rendered("div.banner", box: new Box(0, 0, 100, 40), background: new Rgba(0, 0, 0, 0)),
            "auto-name");

        var bg = Assert.Single(Engine.Diff(pair).Diffs);

        Assert.Equal(new PropDiff("background", "#2563EB", "(transparent)", null, null, 2.0, Severity.Medium, DiffStatus.Missing), bg);
    }

    [Fact]
    public void Transparent_design_fill_is_not_compared()
    {
        var pair = new NodePair(
            Design("d1", "overlay", box: new Box(0, 0, 100, 40), fill: new Rgba(255, 0, 0, 0)),
            Rendered("div.overlay", box: new Box(0, 0, 100, 40), background: new Rgba(0, 0, 255)),
            "auto-name");

        Assert.Empty(Engine.Diff(pair).Diffs);
    }

    [Theory]
    [InlineData(27.0, 0.0, "lineHeight")]   // 行高走尺寸容差(2px):差 3 → 報
    [InlineData(25.5, 0.0, null)]           // 差 1.5 → 不報
    [InlineData(24.0, 0.8, "letterSpacing")] // 字距走字級容差(0.5px):差 0.8 → 報
    [InlineData(24.0, 0.4, null)]
    public void Line_height_and_letter_spacing_use_their_own_tolerances(double lineHeight, double letterSpacing, string? reported)
    {
        var pair = new NodePair(
            Design("d1", "body", DesignNodeType.Text, box: new Box(0, 0, 300, 24),
                text: new Typography(LineHeight: 24, LetterSpacing: 0)),
            Rendered("p", "p", text: "x", box: new Box(0, 0, 300, 24),
                typography: new Typography(LineHeight: lineHeight, LetterSpacing: letterSpacing)),
            "auto-text");

        var props = Engine.Diff(pair).Diffs.Select(d => d.Prop);

        string[] expected = reported is null ? [] : [reported];
        Assert.Equal(expected, props);
    }

    [Fact]
    public void Corner_radius_is_compared_only_when_the_page_reports_one()
    {
        var design = Design("d1", "card", box: new Box(0, 0, 100, 40)) with { CornerRadius = 12 };
        var rounded = Rendered("div.card", box: new Box(0, 0, 100, 40)) with { CornerRadius = 4 };
        var unknown = Rendered("div.card", box: new Box(0, 0, 100, 40));

        var radius = Assert.Single(Engine.Diff(new NodePair(design, rounded, "auto-name")).Diffs);
        Assert.Equal(("cornerRadius", "12", "4", 8.0), (radius.Prop, radius.Expected, radius.Actual, radius.Delta!.Value));
        Assert.Empty(Engine.Diff(new NodePair(design, unknown, "auto-name")).Diffs);
    }

    [Fact]
    public void Child_gap_axis_is_guessed_from_the_layout_when_layout_mode_is_unknown()
    {
        // 手寫 JSON 常沒有 layoutMode:子元素垂直排 → 量 Y 方向的間距;零尺寸的子元素不算
        var column = Rendered("ul", "ul", box: new Box(0, 0, 100, 200), children:
        [
            Rendered("li:nth-of-type(1)", "li", box: new Box(0, 0, 100, 20)),
            Rendered("li:nth-of-type(2)", "li", box: new Box(0, 30, 100, 20)),   // gap 10
            Rendered("li:nth-of-type(3)", "li", box: new Box(0, 30, 0, 0)),     // 隱形,略過
            Rendered("li:nth-of-type(4)", "li", box: new Box(0, 70, 100, 20)),   // gap 20
        ]);
        var row = Rendered("nav", "nav", box: new Box(0, 0, 300, 20), children:
        [
            Rendered("a:nth-of-type(1)", "a", box: new Box(0, 0, 40, 20)),
            Rendered("a:nth-of-type(2)", "a", box: new Box(48, 0, 40, 20)),     // gap 8
        ]);

        Assert.Equal(15, DiffEngine.MeanChildGap(column, layoutMode: null));
        Assert.Equal(8, DiffEngine.MeanChildGap(row, layoutMode: null));
        Assert.Equal(-20, DiffEngine.MeanChildGap(row, layoutMode: "VERTICAL")); // layoutMode 給了就照它,不猜
        Assert.Null(DiffEngine.MeanChildGap(Rendered("div", children: Rendered("p", "p", box: new Box(0, 0, 10, 10))), null));
    }

    [Theory]
    [InlineData(400.0, 400.5, null)]              // 字重是 100 級距:差不到 1 不算落差
    [InlineData(400.0, 500.0, Severity.Medium)]   // 差一級
    [InlineData(400.0, 600.0, Severity.Serious)]  // 差兩級(邊界含)
    [InlineData(700.0, 400.0, Severity.Serious)]  // 方向無關
    public void Font_weight_severity_steps_by_hundreds(double designWeight, double pageWeight, Severity? expected)
    {
        // 字重不走「超出容差倍率」那套:級距本身就是單位,差一級 medium、兩級以上 serious
        var pair = new NodePair(
            Design("d1", "title", DesignNodeType.Text, text: new Typography(FontWeight: designWeight)),
            Rendered("h1", "h1", text: "x", typography: new Typography(FontWeight: pageWeight)),
            "auto-text");

        var diffs = Engine.Diff(pair).Diffs;

        if (expected is null)
        {
            Assert.Empty(diffs);
            return;
        }
        var weight = Assert.Single(diffs);
        Assert.Equal(new PropDiff("fontWeight",
            designWeight.ToString(System.Globalization.CultureInfo.InvariantCulture),
            pageWeight.ToString(System.Globalization.CultureInfo.InvariantCulture),
            Unit: null, Delta: Math.Abs(designWeight - pageWeight), Tolerance: 0, expected.Value), weight);
        Assert.False(weight.Soft); // 字重是硬落差(會擋 gate),只有字型家族是軟的
    }

    [Fact]
    public void Typography_and_text_color_are_compared_only_where_the_page_reports_them()
    {
        // 頁面沒回報的屬性(擷取不到 / 不適用)不是「做錯」,不能生出落差,也不能當成 missing
        var design = Design("d1", "title", DesignNodeType.Text,
            fill: new Rgba(0x11, 0x18, 0x27),
            text: new Typography("Inter", FontSize: 32, FontWeight: 700, LineHeight: 40, LetterSpacing: 1));

        var nothingReported = Rendered("h1", "h1", text: "x");
        Assert.Empty(Engine.Diff(new NodePair(design, nothingReported, "auto-text")).Diffs);

        var partlyReported = Rendered("h1", "h1", text: "x", color: new Rgba(0xFF, 0, 0),
            typography: new Typography(FontSize: 20));
        Assert.Equal(["fontSize", "color"], Engine.Diff(new NodePair(design, partlyReported, "auto-text")).Diffs.Select(d => d.Prop));
    }

    [Fact]
    public void Item_spacing_needs_two_visible_children_on_both_sides()
    {
        // 間距是「相鄰子元素之間」的量:任一邊湊不出一對可量的子元素,就沒有東西可比
        var design = Design("d1", "nav", box: new Box(0, 0, 300, 40), itemSpacing: 16, layoutMode: "HORIZONTAL",
            children: [Design("d2", "a", box: new Box(0, 0, 50, 40)), Design("d3", "b", box: new Box(66, 0, 50, 40))]);
        var oneChild = Rendered("nav", "nav", box: new Box(0, 0, 300, 40),
            children: Rendered("nav > a", "a", box: new Box(0, 0, 50, 40)));
        var invisibleChildren = Rendered("nav", "nav", box: new Box(0, 0, 300, 40), children:
        [
            Rendered("nav > a:nth-of-type(1)", "a", box: new Box(0, 0, 0, 0)),
            Rendered("nav > a:nth-of-type(2)", "a", box: new Box(90, 0, 0, 0)),
        ]);

        Assert.DoesNotContain(Engine.Diff(new NodePair(design, oneChild, "auto-name")).Diffs, d => d.Prop == "itemSpacing");
        Assert.DoesNotContain(Engine.Diff(new NodePair(design, invisibleChildren, "auto-name")).Diffs, d => d.Prop == "itemSpacing");
    }
}
