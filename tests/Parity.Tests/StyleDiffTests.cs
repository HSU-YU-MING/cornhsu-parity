using System.Text.Json.Nodes;
using Parity.Engine;
using Parity.Engine.Comparison;
using Parity.Engine.DesignSources;
using Parity.Engine.DesignSources.Figma;
using Parity.Engine.DesignSources.Snapshot;
using Parity.Engine.ImplementationSources;
using Parity.Engine.Model;
using static Parity.Tests.TestData;

namespace Parity.Tests;

/// <summary>
/// 樣式屬性(斜體、底線、大小寫、對齊、行數、透明度、邊框、陰影、漸層)。全部是軟落差;
/// 任一邊沒有這項資訊就不比,所以舊快照與手寫 JSON 不會多出落差。真瀏覽器那半見 GapLabTests 的 G7～G10。
/// </summary>
public class StyleDiffTests
{
    private static List<PropDiff> Diffs(DesignNode design, RenderedNode rendered, string matchedBy = "auto-name")
        => new DiffEngine(new Tolerances()).Diff(new NodePair(design, rendered, matchedBy)).Diffs.ToList();

    private static DesignNode TextNode(Typography text, string characters = "Hello") => Design("1", "t", DesignNodeType.Text, text: text, characters: characters);

    private static RenderedNode Page(Typography typography, string? text = "Hello") => Rendered("p", "p", text: text, typography: typography);

    private static void AssertSoft(PropDiff d, string prop, string expected, string actual)
    {
        Assert.Equal(prop, d.Prop);
        Assert.Equal(expected, d.Expected);
        Assert.Equal(actual, d.Actual);
        Assert.True(d.Soft);
        Assert.Equal(Severity.Minor, d.Severity);
    }

    // ---------- 文字樣式 ----------

    [Fact]
    public void Font_style_is_compared()
        => AssertSoft(Assert.Single(Diffs(TextNode(new Typography { FontStyle = "normal" }), Page(new Typography { FontStyle = "italic" }))),
            "fontStyle", "normal", "italic");

    [Fact]
    public void Text_decoration_is_compared_regardless_of_word_order()
    {
        AssertSoft(Assert.Single(Diffs(TextNode(new Typography { TextDecoration = "none" }), Page(new Typography { TextDecoration = "underline" }))),
            "textDecoration", "none", "underline");
        Assert.Empty(Diffs(TextNode(new Typography { TextDecoration = "underline line-through" }), Page(new Typography { TextDecoration = "line-through underline" })));
    }

    [Fact]
    public void Text_align_is_compared()
        => AssertSoft(Assert.Single(Diffs(TextNode(new Typography { TextAlign = "center" }), Page(new Typography { TextAlign = "left" }))),
            "textAlign", "center", "left");

    [Fact]
    public void Text_transform_is_compared_when_it_changes_what_you_see()
        => AssertSoft(Assert.Single(Diffs(TextNode(new Typography { TextTransform = "none" }, "Hello"), Page(new Typography { TextTransform = "uppercase" }))),
            "textTransform", "none", "uppercase");

    [Theory]
    [InlineData("none", "uppercase", "HELLO", "hello")]   // 設計稿直接打大寫字母,頁面用 CSS 轉:畫面一樣
    [InlineData("none", "lowercase", "hello", "HELLO")]
    [InlineData("none", "capitalize", "Hello World", "hello world")]
    [InlineData("uppercase", "none", "hello", "HELLO")]   // 反過來:設計稿用 textCase,頁面直接打大寫
    [InlineData("uppercase", "none", "你好", "你好")]     // 中文沒有大小寫
    public void Text_transform_that_looks_the_same_is_not_a_diff(string design, string page, string designText, string pageText)
        => Assert.Empty(Diffs(TextNode(new Typography { TextTransform = design }, designText), Page(new Typography { TextTransform = page }, pageText)));

    [Fact]
    public void Text_style_missing_on_either_side_is_not_compared()
    {
        var empty = new Typography();
        var full = new Typography { FontStyle = "italic", TextDecoration = "underline", TextTransform = "uppercase", TextAlign = "right" };

        Assert.Empty(Diffs(TextNode(empty), Page(full)));
        Assert.Empty(Diffs(TextNode(full), Page(empty)));
    }

    // ---------- 行數 ----------

    private static DesignNode Lines(double boxHeight, double? lineHeight, string? autoResize = null) =>
        Design("1", "t", DesignNodeType.Text, box: new Box(0, 0, 300, boxHeight), text: new Typography(LineHeight: lineHeight), characters: "Hello") with { TextAutoResize = autoResize };

    private static RenderedNode PageLines(int? lines) => Rendered("p", "p", text: "Hello") with { LineCount = lines };

    [Fact]
    public void Line_count_comes_from_box_height_over_line_height()
    {
        var diff = Assert.Single(Diffs(Lines(24, 24), PageLines(5)));

        AssertSoft(diff, "lineCount", "1", "5");
        Assert.Equal(4, diff.Delta);
        Assert.Empty(Diffs(Lines(48, 24), PageLines(2)));
    }

    [Theory]
    [InlineData("NONE")]      // 固定高度:框高跟行數無關
    [InlineData("TRUNCATE")]
    public void A_fixed_height_text_box_says_nothing_about_lines(string autoResize)
        => Assert.Empty(Diffs(Lines(100, 24, autoResize), PageLines(1)));

    [Fact]
    public void Line_count_needs_both_sides()
    {
        Assert.Empty(Diffs(Lines(24, null), PageLines(5)));
        Assert.Empty(Diffs(Lines(24, 24), PageLines(null)));
    }

    [Fact]
    public void A_snapshot_uses_the_measured_line_count_and_an_old_snapshot_is_not_guessed()
    {
        var measured = Lines(48, 24) with { LineCount = 1 };
        Assert.Equal("lineCount", Assert.Single(Diffs(measured, PageLines(2), "selector")).Prop);

        // 舊快照沒有 LineCount:框含 padding,用框高算會誤報
        Assert.Empty(Diffs(Lines(48, 24), PageLines(1), "selector"));
    }

    // ---------- 透明度 ----------

    [Fact]
    public void Opacity_is_compared_with_a_small_tolerance()
    {
        var card = Design("1", "card") with { Opacity = 1 };

        var diff = Assert.Single(Diffs(card, Rendered("div") with { Opacity = 0.2 }));
        AssertSoft(diff, "opacity", "1", "0.2");
        Assert.Equal(0.8, diff.Delta);
        Assert.Empty(Diffs(card, Rendered("div") with { Opacity = 0.995 }));
    }

    // ---------- 邊框 ----------

    private static readonly Insets NoBorder = new(0, 0, 0, 0);
    private static readonly Rgba Black = new(0, 0, 0);
    private static readonly Rgba Red = new(255, 0, 0);

    [Fact]
    public void A_one_pixel_border_counts_even_though_it_is_inside_the_size_tolerance()
    {
        var diff = Assert.Single(Diffs(Design("1", "card") with { BorderWidth = NoBorder }, Rendered("div") with { BorderWidth = new Insets(1, 1, 1, 1) }));

        AssertSoft(diff, "borderWidth", "0", "1");
        Assert.Equal("px", diff.Unit);
    }

    [Fact]
    public void Border_sides_are_one_diff_and_list_every_side_when_they_differ()
    {
        var diff = Assert.Single(Diffs(Design("1", "card") with { BorderWidth = new Insets(0, 0, 1, 0) }, Rendered("div") with { BorderWidth = new Insets(3, 0, 0, 0) }));

        AssertSoft(diff, "borderWidth", "0 0 1 0", "3 0 0 0");
        Assert.Equal(3, diff.Delta);
    }

    [Fact]
    public void Border_color_is_compared_only_when_the_widths_agree()
    {
        var one = new Insets(1, 1, 1, 1);
        AssertSoft(Assert.Single(Diffs(
            Design("1", "card") with { BorderWidth = one, BorderColor = Black },
            Rendered("div") with { BorderWidth = one, BorderColor = Red })), "borderColor", "#000000", "#FF0000");

        // 粗細就不一樣了:同一個根因不報兩次
        Assert.Equal("borderWidth", Assert.Single(Diffs(
            Design("1", "card") with { BorderWidth = new Insets(4, 4, 4, 4), BorderColor = Black },
            Rendered("div") with { BorderWidth = one, BorderColor = Red })).Prop);
    }

    [Fact]
    public void A_border_the_same_color_as_the_fill_cannot_be_seen()
    {
        // 真實案例:Ant Design 的設計稿邊框與底色同色(1px),頁面是透明邊框,兩邊看起來一模一樣
        var one = new Insets(1, 1, 1, 1);
        var blue = new Rgba(24, 144, 255);

        Assert.Empty(Diffs(Design("1", "btn", fill: blue) with { BorderWidth = one, BorderColor = blue }, Rendered("button", background: blue) with { BorderWidth = NoBorder }));

        // 反過來:頁面的邊框跟自己的底色同色,設計稿沒有邊框
        Assert.Empty(Diffs(Design("1", "btn") with { BorderWidth = NoBorder }, Rendered("button", background: blue) with { BorderWidth = one, BorderColor = blue }));
    }

    [Fact]
    public void A_border_of_another_color_is_still_a_border()
    {
        var one = new Insets(1, 1, 1, 1);
        var blue = new Rgba(24, 144, 255);

        Assert.Equal("borderWidth", Assert.Single(Diffs(Design("1", "btn", fill: blue) with { BorderWidth = one, BorderColor = Black }, Rendered("button", background: blue) with { BorderWidth = NoBorder })).Prop);
        Assert.Equal("borderWidth", Assert.Single(Diffs(Design("1", "btn") with { BorderWidth = NoBorder }, Rendered("button", background: blue) with { BorderWidth = one, BorderColor = Black })).Prop);
    }

    [Fact]
    public void A_text_nodes_fill_is_its_text_color_so_a_matching_border_still_counts()
    {
        // 快照的文字節點:Fill 是字色。border: 1px solid currentColor 看得見,不能當成跟底色同色
        var one = new Insets(1, 1, 1, 1);

        Assert.Equal("borderWidth", Assert.Single(Diffs(
            Design("1", "t", DesignNodeType.Text, fill: Black, characters: "Hello") with { BorderWidth = one, BorderColor = Black },
            Rendered("p", "p", text: "Hello") with { BorderWidth = NoBorder })).Prop);
    }

    [Fact]
    public void A_text_node_whose_page_border_blends_into_its_own_background_is_not_compared()
    {
        // 作者網站實測(2026-11-10):格式按鈕「MP3」是快照裡的文字節點,底色與邊框同一個色(只差透明度)。
        // 設計端的文字節點不套「同色 = 沒有邊框」(它的 Fill 是字色),頁面那一邊套了,同一個元素就報成「1px → 0」
        var one = new Insets(1, 1, 1, 1);
        var cream = new Rgba(246, 241, 231, 0.14);
        var creamBackground = new Rgba(246, 241, 231, 0.06);
        var accent = new Rgba(176, 58, 34);

        Assert.Empty(Diffs(
            Design("1", "span", DesignNodeType.Text, fill: new Rgba(246, 241, 231, 0.6), characters: "MP4") with { BorderWidth = one, BorderColor = cream },
            Rendered("span", text: "MP4", background: creamBackground) with { BorderWidth = one, BorderColor = cream }));
        Assert.Empty(Diffs(
            Design("1", "on", DesignNodeType.Text, fill: new Rgba(246, 241, 231), characters: "MP3") with { BorderWidth = one, BorderColor = accent },
            Rendered("span", text: "MP3", background: accent) with { BorderWidth = one, BorderColor = accent }));

        // 反方向也不比:Figma 的文字節點沒有邊框,配到的頁面元素是邊框與底色同色的按鈕——兩邊看起來一樣
        Assert.Empty(Diffs(
            Design("1", "label", DesignNodeType.Text, fill: Black, characters: "OK") with { BorderWidth = NoBorder },
            Rendered("button", text: "OK", background: accent) with { BorderWidth = one, BorderColor = accent }));

        // 頁面的邊框跟底色不同色,照樣比
        Assert.Equal("borderWidth", Assert.Single(Diffs(
            Design("1", "label", DesignNodeType.Text, fill: Black, characters: "OK") with { BorderWidth = NoBorder },
            Rendered("button", text: "OK", background: accent) with { BorderWidth = one, BorderColor = Black })).Prop);
    }

    // ---------- 陰影 ----------

    private static ShadowSpec Shadow(double y, double blur, Rgba? color = null, bool inset = false) => new(0, y, blur, 0, color ?? Black, inset);

    [Fact]
    public void A_shadow_on_only_one_side_is_missing()
    {
        var added = Assert.Single(Diffs(Design("1", "card") with { Shadows = [] }, Rendered("div") with { Shadows = [Shadow(0, 30)] }));
        AssertSoft(added, "shadow", "none", "0px 0px 30px 0px");
        Assert.Equal(DiffStatus.Missing, added.Status);
        Assert.Null(added.Delta);

        Assert.Equal(DiffStatus.Missing, Assert.Single(Diffs(Design("1", "card") with { Shadows = [Shadow(4, 8)] }, Rendered("div") with { Shadows = [] })).Status);
    }

    [Fact]
    public void A_shadow_of_a_different_size_reports_the_largest_difference()
    {
        var diff = Assert.Single(Diffs(Design("1", "card") with { Shadows = [Shadow(4, 8)] }, Rendered("div") with { Shadows = [Shadow(4, 20)] }));

        Assert.Equal("shadow", diff.Prop);
        Assert.Equal(12, diff.Delta);
    }

    [Fact]
    public void Shadow_color_is_its_own_diff()
        => AssertSoft(Assert.Single(Diffs(Design("1", "card") with { Shadows = [Shadow(4, 8, Black)] }, Rendered("div") with { Shadows = [Shadow(4, 8, Red)] })),
            "shadowColor", "#000000", "#FF0000");

    [Fact]
    public void Shadow_layers_are_paired_regardless_of_their_order()
    {
        // Figma 由下往上、CSS 由上往下
        var small = Shadow(1, 2);
        var large = Shadow(10, 20);

        Assert.Empty(Diffs(Design("1", "card") with { Shadows = [small, large] }, Rendered("div") with { Shadows = [large, small] }));
    }

    [Fact]
    public void An_inner_shadow_is_not_a_drop_shadow()
        => Assert.Equal(DiffStatus.Missing, Assert.Single(Diffs(
            Design("1", "card") with { Shadows = [Shadow(4, 8, inset: true)] }, Rendered("div") with { Shadows = [Shadow(4, 8)] })).Status);

    [Fact]
    public void Text_nodes_compare_text_shadow_and_other_nodes_box_shadow()
    {
        var page = Rendered("p", "p", text: "Hello") with { Shadows = [Shadow(4, 8)], TextShadows = [] };

        Assert.Empty(Diffs(Design("1", "t", DesignNodeType.Text, characters: "Hello") with { Shadows = [] }, page));
        Assert.Single(Diffs(Design("1", "card") with { Shadows = [] }, page));
    }

    // ---------- 漸層 ----------

    private static readonly Rgba Blue = new(0, 0, 255);

    [Fact]
    public void A_gradient_on_only_the_design_side_is_missing()
    {
        var diff = Assert.Single(Diffs(Design("1", "hero") with { Gradient = new GradientSpec(Red, Blue) }, Rendered("div")));

        AssertSoft(diff, "gradient", "#FF0000 → #0000FF", "(no gradient)");
        Assert.Equal(DiffStatus.Missing, diff.Status);
    }

    [Fact]
    public void A_gradient_where_the_design_is_a_solid_fill_is_missing()
    {
        var page = Rendered("div", background: Blue) with { Gradient = new GradientSpec(Red, Blue) };

        AssertSoft(Assert.Single(Diffs(Design("1", "hero", fill: Blue), page)), "gradient", "(no gradient)", "#FF0000 → #0000FF");
    }

    [Fact]
    public void An_old_snapshot_is_not_assumed_to_have_no_gradient()
        => Assert.Empty(Diffs(Design("1", "hero", fill: Blue), Rendered("div", background: Blue) with { Gradient = new GradientSpec(Red, Blue) }, "selector"));

    [Theory]
    [InlineData(0, 0.5, 1, 0.5, 90)]      // 左 → 右
    [InlineData(0.5, 0, 0.5, 1, 180)]     // 上 → 下
    [InlineData(1, 0.5, 0, 0.5, 270)]     // 右 → 左
    [InlineData(0.5, 1, 0.5, 0, 0)]       // 下 → 上
    public void Figma_gradient_handles_become_a_css_angle(double x0, double y0, double x1, double y1, double angle)
    {
        var node = Figma($$"""
            { "id": "1", "name": "f", "type": "FRAME", "absoluteBoundingBox": { "x": 0, "y": 0, "width": 200, "height": 100 },
              "fills": [{ "type": "GRADIENT_LINEAR",
                "gradientHandlePositions": [{ "x": {{x0}}, "y": {{y0}} }, { "x": {{x1}}, "y": {{y1}} }, { "x": 0, "y": 0 }],
                "gradientStops": [{ "position": 0, "color": { "r": 1, "g": 0, "b": 0, "a": 1 } }, { "position": 1, "color": { "r": 0, "g": 0, "b": 1, "a": 1 } }] }] }
            """);

        Assert.Equal(angle, node.Gradient!.Angle!.Value, 3);
    }

    [Fact]
    public void Figma_gradient_handles_use_the_real_box_proportions()
    {
        // 對角線:正規化座標是 45 度,但框是 300×100,畫面上的方向要乘上寬高
        var node = Figma("""
            { "id": "1", "name": "f", "type": "FRAME", "absoluteBoundingBox": { "x": 0, "y": 0, "width": 300, "height": 100 },
              "fills": [{ "type": "GRADIENT_LINEAR",
                "gradientHandlePositions": [{ "x": 0, "y": 1 }, { "x": 1, "y": 0 }, { "x": 0, "y": 0 }],
                "gradientStops": [{ "position": 0, "color": { "r": 1, "g": 0, "b": 0, "a": 1 } }, { "position": 1, "color": { "r": 0, "g": 0, "b": 1, "a": 1 } }] }] }
            """);

        Assert.Equal(Math.Atan2(300, 100) * 180 / Math.PI, node.Gradient!.Angle!.Value, 3);
    }

    [Theory]
    [InlineData("GRADIENT_RADIAL", "radial")]
    [InlineData("GRADIENT_ANGULAR", "conic")]
    [InlineData("GRADIENT_DIAMOND", null)]
    public void Figma_gradient_kinds_map_to_css(string figma, string? css)
    {
        var node = Figma($$"""{ "id": "1", "name": "f", "type": "FRAME", "fills": [{ "type": "{{figma}}" }] }""");

        Assert.Equal(css, node.Gradient!.Kind);
        Assert.Null(node.Gradient.Angle);
    }

    [Theory]
    [InlineData("linear-gradient(rgb(255, 0, 0), rgb(0, 0, 255))", "linear", 180.0)]  // 沒寫方向預設往下
    [InlineData("linear-gradient(90deg, rgb(255, 0, 0), rgb(0, 0, 255))", "linear", 90.0)]
    [InlineData("linear-gradient(-90deg, rgb(255, 0, 0), rgb(0, 0, 255))", "linear", 270.0)]
    [InlineData("linear-gradient(0.25turn, rgb(255, 0, 0), rgb(0, 0, 255))", "linear", 90.0)]
    [InlineData("linear-gradient(to right, rgb(255, 0, 0), rgb(0, 0, 255))", "linear", 90.0)]
    [InlineData("linear-gradient(to left, rgb(255, 0, 0), rgb(0, 0, 255))", "linear", 270.0)]
    [InlineData("linear-gradient(to top right, rgb(255, 0, 0), rgb(0, 0, 255))", "linear", null)] // 要看框的比例
    [InlineData("repeating-linear-gradient(45deg, rgb(255, 0, 0), rgb(0, 0, 255))", "linear", 45.0)]
    [InlineData("radial-gradient(rgb(255, 0, 0), rgb(0, 0, 255))", "radial", null)]
    [InlineData("conic-gradient(rgb(255, 0, 0), rgb(0, 0, 255))", "conic", null)]
    public void Css_gradient_kind_and_direction_are_parsed(string css, string kind, double? angle)
    {
        var g = CssEffects.ParseGradient(css)!;

        Assert.Equal(kind, g.Kind);
        Assert.Equal(angle, g.Angle);
        Assert.Equal(new Rgba(255, 0, 0), g.Start);
        Assert.Equal(new Rgba(0, 0, 255), g.End);
    }

    [Fact]
    public void A_gradient_pointing_another_way_is_a_diff_without_a_color_difference()
    {
        var diff = Assert.Single(Diffs(
            Design("1", "hero") with { Gradient = new GradientSpec(Red, Blue) { Kind = "linear", Angle = 90 } },
            Rendered("div") with { Gradient = new GradientSpec(Red, Blue) { Kind = "linear", Angle = 180 } }));

        AssertSoft(diff, "gradient", "#FF0000 → #0000FF, 90deg", "#FF0000 → #0000FF, 180deg");
        Assert.Null(diff.Delta); // 統計上 gradient 的區間是色差,方向不一樣只算條數
    }

    [Theory]
    [InlineData(92)]   // 容差內
    [InlineData(450)]  // 繞一圈
    public void Gradient_angles_within_tolerance_are_the_same(double actual)
        => Assert.Empty(Diffs(
            Design("1", "hero") with { Gradient = new GradientSpec(Red, Blue) { Angle = 90 } },
            Rendered("div") with { Gradient = new GradientSpec(Red, Blue) { Angle = actual } }));

    [Fact]
    public void Swapped_colors_turned_around_are_the_same_gradient()
        => Assert.Empty(Diffs(
            Design("1", "hero") with { Gradient = new GradientSpec(Red, Blue) { Angle = 90 } },
            Rendered("div") with { Gradient = new GradientSpec(Blue, Red) { Angle = 270 } }));

    [Fact]
    public void Swapped_colors_pointing_the_same_way_are_a_diff()
        => Assert.Equal("gradient", Assert.Single(Diffs(
            Design("1", "hero") with { Gradient = new GradientSpec(Red, Blue) { Angle = 90 } },
            Rendered("div") with { Gradient = new GradientSpec(Blue, Red) { Angle = 90 } })).Prop);

    [Fact]
    public void A_different_gradient_kind_is_a_diff()
        => AssertSoft(Assert.Single(Diffs(
            Design("1", "hero") with { Gradient = new GradientSpec(Red, Blue) { Kind = "linear" } },
            Rendered("div") with { Gradient = new GradientSpec(Red, Blue) { Kind = "radial" } })),
            "gradient", "#FF0000 → #0000FF", "radial #FF0000 → #0000FF");

    [Fact]
    public void An_unknown_direction_is_not_compared()
        => Assert.Empty(Diffs(
            Design("1", "hero") with { Gradient = new GradientSpec(Red, Blue) { Angle = 90 } },
            Rendered("div") with { Gradient = new GradientSpec(Red, Blue) })); // to top right

    [Fact]
    public void Gradient_end_colors_are_compared_when_no_direction_is_known()
    {
        var design = Design("1", "hero") with { Gradient = new GradientSpec(Red, Blue) };

        Assert.Empty(Diffs(design, Rendered("div") with { Gradient = new GradientSpec(Blue, Red) }));
        var diff = Assert.Single(Diffs(design, Rendered("div") with { Gradient = new GradientSpec(Red, Black) }));
        Assert.Equal("gradient", diff.Prop);
        Assert.NotNull(diff.Delta);
    }

    [Fact]
    public void A_gradient_whose_colors_could_not_be_read_only_counts_as_having_one()
        => Assert.Empty(Diffs(Design("1", "hero") with { Gradient = new GradientSpec(Red, Blue) }, Rendered("div") with { Gradient = new GradientSpec() }));

    // ---------- 舊資料 ----------

    [Fact]
    public void Nothing_is_compared_when_the_design_does_not_say()
    {
        var page = Rendered("div") with
        {
            Opacity = 0.2,
            BorderWidth = new Insets(4, 4, 4, 4),
            BorderColor = Red,
            Shadows = [Shadow(0, 30)],
            LineCount = 3,
        };

        Assert.Empty(Diffs(Design("1", "card"), page));
    }

    // ---------- CSS 解析 ----------

    [Theory]
    [InlineData("none", 0)]
    [InlineData("rgb(0, 0, 0) 0px 0px 30px 0px", 1)]
    [InlineData("rgba(0, 0, 0, 0.5) 0px 4px 8px 0px, rgb(255, 0, 0) 1px 1px 2px 0px inset", 2)]
    [InlineData("rgba(0, 0, 0, 0) 0px 0px 0px 0px, rgba(0, 0, 0, 0) 0px 0px 0px 0px", 0)] // Tailwind 的預設值
    [InlineData("rgb(0, 0, 0) 0px 0px 0px 0px", 0)]                                        // 沒有位移、模糊、擴散:看不見
    [InlineData("rgb(0, 0, 0) 1px 2px", 1)]                                                // text-shadow 沒有模糊也合法
    public void Shadows_are_parsed_and_invisible_ones_dropped(string css, int count)
        => Assert.Equal(count, CssEffects.ParseShadows(css)!.Count);

    [Fact]
    public void A_shadow_keeps_its_values()
    {
        var s = Assert.Single(CssEffects.ParseShadows("rgba(10, 20, 30, 0.5) 1px 2px 3px 4px inset")!);

        Assert.Equal(new ShadowSpec(1, 2, 3, 4, new Rgba(10, 20, 30, 0.5), true), s);
        Assert.Null(CssEffects.ParseShadows(null));
    }

    [Fact]
    public void Gradients_are_parsed_to_their_end_colors()
    {
        var g = CssEffects.ParseGradient("linear-gradient(90deg, rgb(255, 0, 0) 0%, rgb(0, 255, 0) 50%, rgb(0, 0, 255) 100%)")!;

        Assert.Equal(new Rgba(255, 0, 0), g.Start);
        Assert.Equal(new Rgba(0, 0, 255), g.End);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("none")]
    [InlineData("url(\"https://x/a.png\")")]
    public void No_gradient_is_null(string? css) => Assert.Null(CssEffects.ParseGradient(css));

    [Fact]
    public void The_first_gradient_layer_is_used_and_unreadable_colors_still_count_as_a_gradient()
    {
        Assert.Equal(new Rgba(1, 2, 3), CssEffects.ParseGradient("url(x.png), linear-gradient(rgb(1, 2, 3), rgb(4, 5, 6)), linear-gradient(rgb(7, 8, 9), rgb(0, 0, 0))")!.Start);
        // 讀不懂的色域照樣算有漸層,只是沒有頭尾色(lab() 2.5.0 起讀得懂,換成 rec2020)
        Assert.Null(CssEffects.ParseGradient("linear-gradient(color(rec2020 0.5 0 0), color(rec2020 0.6 0 0))")!.Start);
        Assert.NotNull(CssEffects.ParseGradient("linear-gradient(lab(50 0 0), lab(60 0 0))")!.Start);
    }

    // ---------- Figma ----------

    private static DesignNode Figma(string json) => FigmaNodeParser.Parse(JsonNode.Parse(json)!);

    [Fact]
    public void Figma_text_style_defaults_are_known_values_not_unknown()
    {
        // Figma 在預設值時會省略這些欄位
        var t = Figma("""{ "id": "1", "name": "t", "type": "TEXT", "characters": "x", "style": { "fontSize": 16 } }""");

        Assert.Equal("normal", t.Text!.FontStyle);
        Assert.Equal("none", t.Text.TextDecoration);
        Assert.Equal("none", t.Text.TextTransform);
        Assert.Null(t.Text.TextAlign);
        Assert.Equal("NONE", t.TextAutoResize);
        Assert.Equal(1, t.Opacity);
    }

    [Theory]
    [InlineData("""{ "fontStyle": "Regular" }""", "normal")]     // 真實回應:沒有 italic 欄位
    [InlineData("""{ "fontStyle": "Bold" }""", "normal")]
    [InlineData("""{ "fontStyle": "Bold Italic", "italic": true }""", "italic")]
    [InlineData("""{ "italic": true }""", "italic")]
    [InlineData("""{ "fontStyle": "Italic" }""", "italic")]      // 字型名稱有寫、italic 欄位漏掉
    [InlineData("""{ "fontStyle": "Light Oblique" }""", "italic")]
    public void Figma_italic_is_read_from_either_field(string style, string expected)
        => Assert.Equal(expected, Figma($$"""{ "id": "1", "name": "t", "type": "TEXT", "style": {{style}} }""").Text!.FontStyle);

    [Fact]
    public void A_real_figma_text_node_parses_as_expected()
    {
        // 2026-11-06 真實回應的節錄(Carbon 的 Button 文字):預設值都被省略,textAutoResize 在 style 裡
        var node = Figma("""
            { "id": "1854:1779", "name": "Button", "type": "TEXT", "characters": "Button", "strokeWeight": 1, "strokeAlign": "OUTSIDE", "strokes": [], "effects": [],
              "layoutSizingHorizontal": "HUG", "layoutSizingVertical": "HUG", "layoutAlign": "INHERIT", "layoutGrow": 0,
              "absoluteBoundingBox": { "x": 0, "y": 0, "width": 44, "height": 18 },
              "fills": [{ "type": "SOLID", "color": { "r": 1, "g": 1, "b": 1, "a": 1 } }],
              "style": { "fontFamily": "IBM Plex Sans", "fontPostScriptName": "IBMPlexSans", "fontStyle": "Regular", "fontWeight": 400, "textAutoResize": "WIDTH_AND_HEIGHT",
                "fontSize": 14, "textAlignHorizontal": "LEFT", "textAlignVertical": "TOP", "letterSpacing": 0.16, "lineHeightPx": 18, "lineHeightUnit": "PIXELS" } }
            """);

        Assert.Equal("normal", node.Text!.FontStyle);
        Assert.Equal("none", node.Text.TextDecoration);
        Assert.Equal("none", node.Text.TextTransform);
        Assert.Null(node.Text.TextAlign); // 文字框隨內容縮放:對齊沒有視覺意義
        Assert.Equal("WIDTH_AND_HEIGHT", node.TextAutoResize);
        Assert.Null(node.BorderWidth);    // 文字的 stroke 不是 CSS 的 border,strokeWeight 1 也不算
        Assert.Empty(node.Shadows!);
        Assert.Equal(1, node.Opacity);
    }

    [Fact]
    public void A_real_figma_button_with_a_border_parses_as_expected()
    {
        // 2026-11-06 真實回應的節錄(Codex 的按鈕)
        var node = Figma("""
            { "id": "10019:92457", "name": "Button", "type": "COMPONENT", "strokeWeight": 1, "strokeAlign": "INSIDE", "effects": [],
              "strokes": [{ "blendMode": "NORMAL", "type": "SOLID", "color": { "r": 0.4470588, "g": 0.4666667, "b": 0.4901961, "a": 1 } }],
              "layoutMode": "HORIZONTAL", "primaryAxisAlignItems": "CENTER", "counterAxisAlignItems": "CENTER",
              "layoutSizingHorizontal": "HUG", "layoutSizingVertical": "FIXED", "cornerRadius": 2 }
            """);

        Assert.Equal(new Insets(1, 1, 1, 1), node.BorderWidth);
        Assert.Equal("#72777D", node.BorderColor!.Value.ToHex());
        Assert.Equal("CENTER", node.PrimaryAxisAlignItems);
    }

    [Fact]
    public void A_real_figma_frame_without_strokes_has_no_border_even_with_a_stroke_weight()
    {
        // 2026-11-06 真實回應:沒有 strokes 時 strokeWeight 仍然是 1
        var node = Figma("""{ "id": "1", "name": "f", "type": "FRAME", "strokeWeight": 1, "strokeAlign": "INSIDE", "strokes": [], "effects": [], "layoutMode": "VERTICAL", "layoutWrap": "NO_WRAP" }""");

        Assert.Equal(new Insets(0, 0, 0, 0), node.BorderWidth);
        Assert.Null(node.BorderColor);
        Assert.Equal("MIN", node.PrimaryAxisAlignItems); // 預設值被省略
    }

    [Theory]
    [InlineData("UNDERLINE", "underline")]
    [InlineData("STRIKETHROUGH", "line-through")]
    public void Figma_text_decoration_maps_to_css(string figma, string css)
        => Assert.Equal(css, Figma($$"""{ "id": "1", "name": "t", "type": "TEXT", "style": { "textDecoration": "{{figma}}" } }""").Text!.TextDecoration);

    [Theory]
    [InlineData("UPPER", "uppercase")]
    [InlineData("LOWER", "lowercase")]
    [InlineData("TITLE", "capitalize")]
    [InlineData("SMALL_CAPS", "small-caps")]
    [InlineData("SMALL_CAPS_FORCED", "small-caps")]
    public void Figma_text_case_maps_to_css(string figma, string css)
        => Assert.Equal(css, Figma($$"""{ "id": "1", "name": "t", "type": "TEXT", "style": { "textCase": "{{figma}}" } }""").Text!.TextTransform);

    [Fact]
    public void Figma_alignment_is_dropped_when_the_box_hugs_its_text()
    {
        Assert.Equal("center", Figma("""{ "id": "1", "name": "t", "type": "TEXT", "style": { "textAlignHorizontal": "CENTER", "textAutoResize": "HEIGHT" } }""").Text!.TextAlign);
        Assert.Null(Figma("""{ "id": "1", "name": "t", "type": "TEXT", "style": { "textAlignHorizontal": "CENTER", "textAutoResize": "WIDTH_AND_HEIGHT" } }""").Text!.TextAlign);
    }

    [Fact]
    public void Figma_strokes_become_a_border_only_on_box_like_nodes()
    {
        var frame = Figma("""
            { "id": "1", "name": "f", "type": "FRAME", "strokeWeight": 2,
              "strokes": [{ "type": "SOLID", "color": { "r": 1, "g": 0, "b": 0, "a": 1 } }] }
            """);
        Assert.Equal(new Insets(2, 2, 2, 2), frame.BorderWidth);
        Assert.Equal(new Rgba(255, 0, 0), frame.BorderColor);

        var each = Figma("""
            { "id": "1", "name": "f", "type": "FRAME", "strokeWeight": 2,
              "individualStrokeWeights": { "top": 0, "right": 0, "bottom": 1, "left": 0 },
              "strokes": [{ "type": "SOLID", "color": { "r": 0, "g": 0, "b": 0, "a": 1 } }] }
            """);
        Assert.Equal(new Insets(0, 0, 1, 0), each.BorderWidth);

        Assert.Equal(new Insets(0, 0, 0, 0), Figma("""{ "id": "1", "name": "f", "type": "FRAME", "strokeWeight": 1, "strokes": [] }""").BorderWidth);
        Assert.Equal(new Insets(0, 0, 0, 0), Figma("""
            { "id": "1", "name": "f", "type": "FRAME", "strokeWeight": 1,
              "strokes": [{ "type": "SOLID", "visible": false, "color": { "r": 0, "g": 0, "b": 0 } }] }
            """).BorderWidth);

        // 文字描邊、線條本身都不是 CSS 的 border
        const string Stroke = """ "strokeWeight": 2, "strokes": [{ "type": "SOLID", "color": { "r": 0, "g": 0, "b": 0 } }] """;
        Assert.Null(Figma($$"""{ "id": "1", "name": "t", "type": "TEXT", {{Stroke}} }""").BorderWidth);
        Assert.Null(Figma($$"""{ "id": "1", "name": "l", "type": "LINE", {{Stroke}} }""").BorderWidth);
    }

    [Fact]
    public void Figma_effects_become_shadows()
    {
        var node = Figma("""
            { "id": "1", "name": "f", "type": "FRAME", "effects": [
              { "type": "DROP_SHADOW", "color": { "r": 0, "g": 0, "b": 0, "a": 0.25 }, "offset": { "x": 0, "y": 4 }, "radius": 8, "spread": 2 },
              { "type": "INNER_SHADOW", "color": { "r": 1, "g": 0, "b": 0, "a": 1 }, "offset": { "x": 1, "y": 1 }, "radius": 2 },
              { "type": "DROP_SHADOW", "visible": false, "color": { "r": 0, "g": 0, "b": 0, "a": 1 }, "offset": { "x": 9, "y": 9 }, "radius": 9 },
              { "type": "LAYER_BLUR", "radius": 4 } ] }
            """);

        Assert.Equal([new ShadowSpec(0, 4, 8, 2, new Rgba(0, 0, 0, 0.25)), new ShadowSpec(1, 1, 2, 0, new Rgba(255, 0, 0), true)], node.Shadows!);
        Assert.Empty(Figma("""{ "id": "1", "name": "f", "type": "FRAME" }""").Shadows!);
    }

    [Fact]
    public void Figma_gradient_fills_are_read_in_stop_order()
    {
        var node = Figma("""
            { "id": "1", "name": "f", "type": "FRAME", "fills": [{ "type": "GRADIENT_LINEAR", "gradientStops": [
              { "position": 1, "color": { "r": 0, "g": 0, "b": 1, "a": 1 } },
              { "position": 0, "color": { "r": 1, "g": 0, "b": 0, "a": 1 } } ] }] }
            """);

        Assert.Equal(new Rgba(255, 0, 0), node.Gradient!.Start);
        Assert.Equal(new Rgba(0, 0, 255), node.Gradient.End);
        Assert.Equal("linear", node.Gradient.Kind);
        Assert.Null(node.Gradient.Angle); // 沒有 gradientHandlePositions
        Assert.Null(Figma("""{ "id": "1", "name": "f", "type": "FRAME", "fills": [{ "type": "SOLID", "color": { "r": 1, "g": 1, "b": 1 } }] }""").Gradient);
    }

    // ---------- 快照 ----------

    [Fact]
    public void A_snapshot_freezes_the_new_style_values()
    {
        var shadow = Shadow(4, 8);
        var body = Rendered("body", children:
        [
            Rendered("#card", classes: "card") with
            {
                Opacity = 0.5, BorderWidth = new Insets(1, 1, 1, 1), BorderColor = Red, Shadows = [shadow], Gradient = new GradientSpec(Red, Blue),
            },
            Rendered("#title", "h1", text: "Title", typography: new Typography { FontStyle = "italic", TextTransform = "uppercase" }) with
            {
                LineCount = 2, Shadows = [shadow], TextShadows = [],
            },
        ]);

        var frame = SnapshotBuilder.ToFrame(body, "/");
        var card = frame.Children[0];
        var title = frame.Children[1];

        Assert.Equal(0.5, card.Opacity);
        Assert.Equal(new Insets(1, 1, 1, 1), card.BorderWidth);
        Assert.Equal(Red, card.BorderColor);
        Assert.Equal([shadow], card.Shadows!);
        Assert.Equal(new GradientSpec(Red, Blue), card.Gradient);
        Assert.Null(card.LineCount);
        Assert.Equal(2, title.LineCount);
        Assert.Null(title.Shadows); // 文字節點記 text-shadow,這裡沒有;box-shadow 不算
        Assert.Equal("italic", title.Text!.FontStyle);
    }

    private static async Task<DesignNode> RoundTrip(DesignNode frame, bool styleDefaultsOmitted)
    {
        using var dir = new TempDir();
        var path = dir.Write("snapshot.json", Parity.Cli.ReportJson.SerializeSnapshotTree(frame, styleDefaultsOmitted: styleDefaultsOmitted));
        return await new Parity.Engine.DesignSources.Json.JsonDesignSource().GetFrameAsync(new DesignRef(path, ""));
    }

    private static RenderedNode PlainPage() => Rendered("body", children:
    [
        Rendered("#card", classes: "card") with { Opacity = 1, BorderWidth = NoBorder, Shadows = [] },
        Rendered("#title", "h1", text: "Title", typography: new Typography { FontStyle = "normal", TextDecoration = "none", TextTransform = "none" }) with { TextShadows = [] },
    ]);

    [Fact]
    public void A_snapshot_only_stores_values_that_are_not_the_default()
    {
        // 全部都寫的話,快照會大 34～44%(非作者網站實測)
        var frame = SnapshotBuilder.ToFrame(PlainPage(), "/");
        var card = frame.Children[0];
        var title = frame.Children[1];

        Assert.Null(card.Opacity);
        Assert.Null(card.BorderWidth);
        Assert.Null(card.Shadows);
        Assert.Null(title.Text!.FontStyle);
        Assert.Null(title.Text.TextDecoration);
        Assert.Null(title.Text.TextTransform);
    }

    [Fact]
    public async Task Reading_it_back_restores_the_defaults_so_a_new_shadow_is_still_a_diff()
    {
        var back = await RoundTrip(SnapshotBuilder.ToFrame(PlainPage(), "/"), styleDefaultsOmitted: true);
        var card = back.Children[0];
        var title = back.Children[1];

        Assert.Equal(1, card.Opacity);
        Assert.Equal(NoBorder, card.BorderWidth);
        Assert.Empty(card.Shadows!);
        Assert.Equal("normal", title.Text!.FontStyle);
        Assert.Equal("none", title.Text.TextDecoration);
        Assert.Equal("none", title.Text.TextTransform);
        Assert.Null(title.Text.TextAlign); // null 代表對齊沒有視覺意義,不是預設值

        Assert.Equal("shadow", Assert.Single(Diffs(card, Rendered("div") with { Shadows = [Shadow(0, 30)] }, "selector")).Prop);
    }

    [Fact]
    public async Task A_snapshot_without_the_marker_stays_unknown_so_upgrading_adds_no_diffs()
    {
        // 舊版拍的快照沒有這些欄位:沒寫是「不知道」,不能當成預設值去比
        var back = await RoundTrip(SnapshotBuilder.ToFrame(PlainPage(), "/"), styleDefaultsOmitted: false);
        var card = back.Children[0];

        Assert.Null(card.Opacity);
        Assert.Null(card.Shadows);
        Assert.Empty(Diffs(card, Rendered("div") with { Opacity = 0.2, BorderWidth = new Insets(4, 4, 4, 4), Shadows = [Shadow(0, 30)] }, "selector"));
    }

    [Fact]
    public async Task Non_default_values_survive_the_round_trip()
    {
        var shadow = Shadow(4, 8);
        var page = Rendered("body", children:
        [
            Rendered("#card", classes: "card") with { Opacity = 0.5, BorderWidth = new Insets(1, 2, 3, 4), BorderColor = Red, Shadows = [shadow], Gradient = new GradientSpec(Red, Blue) },
            Rendered("#title", "h1", text: "Title", typography: new Typography { FontStyle = "italic", TextDecoration = "underline", TextTransform = "uppercase", TextAlign = "center" }) with { LineCount = 2 },
        ]);

        var back = await RoundTrip(SnapshotBuilder.ToFrame(page, "/"), styleDefaultsOmitted: true);
        var card = back.Children[0];
        var title = back.Children[1];

        Assert.Equal(0.5, card.Opacity);
        Assert.Equal(new Insets(1, 2, 3, 4), card.BorderWidth);
        Assert.Equal(Red, card.BorderColor);
        Assert.Equal(shadow, Assert.Single(card.Shadows!));
        Assert.Equal(new GradientSpec(Red, Blue), card.Gradient);
        Assert.Equal(2, title.LineCount);
        Assert.Equal("italic", title.Text!.FontStyle);
        Assert.Equal("underline", title.Text.TextDecoration);
        Assert.Equal("uppercase", title.Text.TextTransform);
        Assert.Equal("center", title.Text.TextAlign);
    }
}
